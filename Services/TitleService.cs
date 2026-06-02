using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Newtonsoft.Json;
using AWS.Logger.Log4net;
using log4net;

namespace LandTitleRegistration.Services
{
    public class TitleService
    {
        private readonly IConfiguration _configuration;
        private readonly IAmazonSecretsManager _secretsManager;
        private readonly IAmazonSimpleSystemsManagement _ssmClient;
        private readonly IConnectionMultiplexer _redis;
        private static readonly ILog Log = LogManager.GetLogger(typeof(TitleService));

        // Connection string and secrets loaded from AWS Secrets Manager
        private string _connectionString;
        private string _govApiKey;

        public TitleService(
            IConfiguration configuration,
            IAmazonSimpleSystemsManagement ssmClient,
            IConnectionMultiplexer redis)
        {
            _configuration = configuration;
            _ssmClient = ssmClient;
            _redis = redis;
            
            // Initialize AWS Secrets Manager client
            _secretsManager = new AmazonSecretsManagerClient();

            // Configure CloudWatch Logs appender for log4net
            ConfigureCloudWatchLogging();

            // Load secrets from AWS Secrets Manager
            InitializeSecretsAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Configures log4net to use AWS CloudWatch Logs appender
        /// </summary>
        private void ConfigureCloudWatchLogging()
        {
            var logConfig = new AWSLoggerConfig
            {
                LogGroup = _configuration["Logging:LogGroup"] ?? "/aws/landtitle/application",
                Region = _configuration["AWS:Region"] ?? "us-east-1"
            };

            // CloudWatch appender will be configured via log4net.config
            // This ensures logs are streamed to CloudWatch and survive pod restarts
        }

        /// <summary>
        /// Loads connection strings and API keys from AWS Secrets Manager
        /// </summary>
        private async Task InitializeSecretsAsync()
        {
            try
            {
                var secretPrefix = _configuration["AWS:SecretsManagerPrefix"] ?? "landtitle/";

                // Retrieve database connection string from Secrets Manager
                var dbSecretName = $"{secretPrefix}database/connection-string";
                var dbSecret = await GetSecretAsync(dbSecretName);
                _connectionString = dbSecret;

                // Retrieve Government API key from Secrets Manager
                var apiKeySecretName = $"{secretPrefix}api-keys/government-registry";
                _govApiKey = await GetSecretAsync(apiKeySecretName);

                Log.Info("Successfully loaded secrets from AWS Secrets Manager");
            }
            catch (Exception ex)
            {
                Log.Error("Failed to load secrets from AWS Secrets Manager", ex);
                
                // Fallback to environment variables (not recommended for production)
                _connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
                _govApiKey = Environment.GetEnvironmentVariable("GOV_API_KEY");
                
                if (string.IsNullOrEmpty(_connectionString))
                {
                    throw new InvalidOperationException(
                        "Database connection string not found in Secrets Manager or environment variables");
                }
            }
        }

        /// <summary>
        /// Retrieves a secret value from AWS Secrets Manager
        /// </summary>
        private async Task<string> GetSecretAsync(string secretName)
        {
            var request = new GetSecretValueRequest
            {
                SecretId = secretName
            };

            var response = await _secretsManager.GetSecretValueAsync(request);
            return response.SecretString;
        }

        public async Task<Dictionary<string, object>> CreateRegistrationAsync(
            string ownerName, string parcelId,
            string propertyAddress, string titleType)
        {
            // Use UTC timestamp for cloud-native time handling
            var titleRef = "LT-" + DateTimeOffset.UtcNow.Ticks.ToString().Substring(10);

            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                // Use parameterized queries to prevent SQL injection
                var sql = "INSERT INTO TitleRegistrations " +
                    "(TitleRef, OwnerName, ParcelId, PropertyAddress, TitleType, RegisteredDate) " +
                    "VALUES (@TitleRef, @OwnerName, @ParcelId, @PropertyAddress, @TitleType, GETUTCDATE())";

                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@TitleRef", titleRef);
                    cmd.Parameters.AddWithValue("@OwnerName", ownerName);
                    cmd.Parameters.AddWithValue("@ParcelId", parcelId);
                    cmd.Parameters.AddWithValue("@PropertyAddress", propertyAddress);
                    cmd.Parameters.AddWithValue("@TitleType", titleType);
                    
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // Use SHA-256 instead of SHA-1 for secure hashing
            string confirmCode = ComputeSha256Hash(titleRef + ownerName);

            var result = new Dictionary<string, object>
            {
                ["titleRef"] = titleRef,
                ["ownerName"] = ownerName,
                ["parcelId"] = parcelId,
                ["address"] = propertyAddress,
                ["type"] = titleType,
                ["confirmation"] = confirmCode,
                ["registeredAt"] = DateTimeOffset.UtcNow.ToString("o")
            };
            
            Log.Info($"Registration created: {titleRef}");
            return result;
        }

        public async Task<Dictionary<string, object>> GetTitleByParcelAsync(string parcelId)
        {
            // Use parameterized query to prevent SQL injection
            var sql = "SELECT * FROM TitleRegistrations WHERE ParcelId = @ParcelId";
            var result = new Dictionary<string, object>();
            
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@ParcelId", parcelId);
                    
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                result[reader.GetName(i)] = reader.GetValue(i)?.ToString();
                            }
                        }
                    }
                }
            }
            
            return result;
        }

        /// <summary>
        /// Calculates registration fee based on title type and land value
        /// </summary>
        public decimal CalculateRegistrationFee(string titleType, decimal landValue,
            string ownerCategory, string region, bool isFirstRegistration)
        {
            decimal baseFee = 0m;
            if (titleType == "FREEHOLD")       baseFee = 500m;
            else if (titleType == "LEASEHOLD") baseFee = 350m;
            else if (titleType == "COMMONHOLD") baseFee = 420m;
            else if (titleType == "ABSOLUTE")  baseFee = 600m;
            else                               baseFee = 300m;

            if (landValue > 1000000m)      baseFee += landValue * 0.004m;
            else if (landValue > 500000m)  baseFee += landValue * 0.003m;
            else if (landValue > 100000m)  baseFee += landValue * 0.002m;

            if (ownerCategory == "COMPANY")     baseFee *= 1.25m;
            else if (ownerCategory == "CHARITY") baseFee *= 0.75m;
            else if (ownerCategory == "GOVERNMENT") baseFee = 0m;

            if (region == "LONDON")   baseFee *= 1.15m;
            else if (region == "SCOTLAND") baseFee *= 0.90m;

            if (isFirstRegistration) baseFee *= 0.50m;

            return Math.Round(baseFee, 2);
        }

        /// <summary>
        /// Validates if the title type is valid
        /// </summary>
        public bool IsTitleTypeValid(string titleType)
        {
            return titleType == "FREEHOLD"   || titleType == "LEASEHOLD" ||
                   titleType == "COMMONHOLD" || titleType == "ABSOLUTE";
        }

        /// <summary>
        /// Generates monthly report using Government Land Registry API
        /// </summary>
        public async Task<string> GenerateMonthlyReportAsync(string month, string year)
        {
            // Use HTTPS and pass API key in Authorization header instead of query string
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_govApiKey}");
                client.Timeout = TimeSpan.FromSeconds(30);

                // Discover service endpoint from Parameter Store
                var serviceUrl = await GetParameterAsync("/landtitle/service-urls/gov-registry") 
                    ?? "https://gov.landregistry.internal/reports";

                var requestUrl = $"{serviceUrl}?month={Uri.EscapeDataString(month)}&year={Uri.EscapeDataString(year)}";
                
                var response = await client.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();
                
                var content = await response.Content.ReadAsStringAsync();
                Log.Info($"Monthly report generated for {month}/{year}");
                
                return content;
            }
        }

        /// <summary>
        /// Retrieves a parameter value from AWS Systems Manager Parameter Store
        /// </summary>
        private async Task<string> GetParameterAsync(string parameterName)
        {
            try
            {
                var request = new GetParameterRequest
                {
                    Name = parameterName,
                    WithDecryption = true
                };

                var response = await _ssmClient.GetParameterAsync(request);
                return response.Parameter.Value;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to retrieve parameter {parameterName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Searches for titles by owner name
        /// </summary>
        public async Task<List<string>> SearchByOwnerAsync(string ownerName)
        {
            // Use parameterized query with LIKE to prevent SQL injection
            var sql = "SELECT TitleRef FROM TitleRegistrations WHERE OwnerName LIKE @OwnerName";
            var refs = new List<string>();
            
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@OwnerName", $"%{ownerName}%");
                    
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            refs.Add(reader.GetString(0));
                        }
                    }
                }
            }
            
            return refs;
        }

        /// <summary>
        /// Computes SHA-256 hash for secure confirmation codes
        /// Replaces SHA-1 which is cryptographically broken
        /// </summary>
        private string ComputeSha256Hash(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder();
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
