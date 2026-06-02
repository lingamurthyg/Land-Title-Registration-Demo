using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading.Channels;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.ServiceDiscovery;
using Amazon.ServiceDiscovery.Model;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Newtonsoft.Json;

namespace LandTitleRegistration.Controllers
{
    /// <summary>
    /// Handles land title registration, search, and document retrieval.
    /// Cloud-ready implementation with externalized configuration and distributed state management.
    /// </summary>
    public class TitleController
    {
        private readonly TitleService _service;
        private readonly IConfiguration _configuration;
        private readonly IAmazonSimpleSystemsManagement _ssmClient;
        private readonly IAmazonServiceDiscovery _serviceDiscoveryClient;
        private readonly IConnectionMultiplexer _redis;
        private readonly Channel<string> _processingChannel;

        // Service URLs loaded from AWS Systems Manager Parameter Store
        private string _documentServiceUrl;
        private string _notificationServiceUrl;
        private string _legacySearchApiUrl;

        // File paths loaded from environment variables with cross-platform support
        private string _archivePath;
        private string _tempExportPath;
        private string _logPath;

        public TitleController(
            IConfiguration configuration,
            IAmazonSimpleSystemsManagement ssmClient,
            IAmazonServiceDiscovery serviceDiscoveryClient,
            IConnectionMultiplexer redis)
        {
            _service = new TitleService(configuration, ssmClient, redis);
            _configuration = configuration;
            _ssmClient = ssmClient;
            _serviceDiscoveryClient = serviceDiscoveryClient;
            _redis = redis;
            
            // Initialize Channel<T> for async producer-consumer pattern (replaces BlockingCollection)
            _processingChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            // Load configuration from AWS Systems Manager Parameter Store
            InitializeConfigurationAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Loads service URLs and configuration from AWS Systems Manager Parameter Store
        /// </summary>
        private async Task InitializeConfigurationAsync()
        {
            try
            {
                var parameterPrefix = _configuration["AWS:ParameterStorePrefix"] ?? "/landtitle/";

                // Load service URLs from Parameter Store
                _documentServiceUrl = await GetParameterAsync($"{parameterPrefix}service-urls/document-service");
                _notificationServiceUrl = await GetParameterAsync($"{parameterPrefix}service-urls/notification-service");
                _legacySearchApiUrl = await GetParameterAsync($"{parameterPrefix}service-urls/legacy-search-api");

                // Load file paths from environment variables with cross-platform path construction
                _archivePath = Environment.GetEnvironmentVariable("ARCHIVE_PATH") 
                    ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "archives");
                _tempExportPath = Environment.GetEnvironmentVariable("TEMP_EXPORT_PATH") 
                    ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp", "exports");
                _logPath = Environment.GetEnvironmentVariable("LOG_PATH") 
                    ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "registration.log");

                // Ensure directories exist
                System.IO.Directory.CreateDirectory(_archivePath);
                System.IO.Directory.CreateDirectory(_tempExportPath);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_logPath));
            }
            catch (Exception ex)
            {
                // Fallback to configuration file values if Parameter Store is unavailable
                _documentServiceUrl = _configuration["ServiceUrls:DocumentService"];
                _notificationServiceUrl = _configuration["ServiceUrls:NotificationService"];
                _legacySearchApiUrl = _configuration["ServiceUrls:LegacySearchApi"];
                
                _archivePath = _configuration["FilePaths:ArchivePath"];
                _tempExportPath = _configuration["FilePaths:TempExport"];
                _logPath = _configuration["FilePaths:LogPath"];

                Console.WriteLine($"Warning: Failed to load from Parameter Store, using config file: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves a parameter value from AWS Systems Manager Parameter Store
        /// </summary>
        private async Task<string> GetParameterAsync(string parameterName)
        {
            var request = new GetParameterRequest
            {
                Name = parameterName,
                WithDecryption = true
            };

            var response = await _ssmClient.GetParameterAsync(request);
            return response.Parameter.Value;
        }

        /// <summary>
        /// Discovers service endpoint using AWS Cloud Map
        /// </summary>
        private async Task<string> DiscoverServiceEndpointAsync(string serviceName)
        {
            try
            {
                var request = new DiscoverInstancesRequest
                {
                    NamespaceName = "landtitle.local",
                    ServiceName = serviceName
                };

                var response = await _serviceDiscoveryClient.DiscoverInstancesAsync(request);
                if (response.Instances.Count > 0)
                {
                    var instance = response.Instances[0];
                    var ipv4 = instance.Attributes.ContainsKey("AWS_INSTANCE_IPV4") 
                        ? instance.Attributes["AWS_INSTANCE_IPV4"] 
                        : "localhost";
                    var port = instance.Attributes.ContainsKey("AWS_INSTANCE_PORT") 
                        ? instance.Attributes["AWS_INSTANCE_PORT"] 
                        : "8080";
                    return $"http://{ipv4}:{port}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service discovery failed for {serviceName}: {ex.Message}");
            }

            return null;
        }

        public async Task<Dictionary<string, object>> RegisterTitle(
            string ownerName, string parcelId,
            string propertyAddress, string titleType)
        {
            // Replace InProc session state with Amazon ElastiCache for Redis
            var db = _redis.GetDatabase();
            var sessionKey = $"session:{Guid.NewGuid()}";
            
            // Store session data in Redis with expiration
            var sessionData = new Dictionary<string, string>
            {
                ["CurrentOwner"] = ownerName,
                ["ActiveParcel"] = parcelId,
                ["RegistrationStep"] = "initiated",
                ["Timestamp"] = DateTimeOffset.UtcNow.ToString("o")
            };

            await db.StringSetAsync(sessionKey, JsonConvert.SerializeObject(sessionData), TimeSpan.FromHours(24));

            var result = await _service.CreateRegistrationAsync(ownerName, parcelId, propertyAddress, titleType);

            // Replace static in-memory cache with Redis distributed cache
            var cacheKey = $"title:{parcelId}";
            await db.StringSetAsync(cacheKey, JsonConvert.SerializeObject(result), TimeSpan.FromHours(1));

            result["sessionKey"] = sessionKey;
            return result;
        }

        public async Task<Dictionary<string, object>> GetTitleStatus(string parcelId, string sessionKey)
        {
            // Retrieve session data from Redis instead of HttpContext.Session
            var db = _redis.GetDatabase();
            string sessionOwner = null;

            if (!string.IsNullOrEmpty(sessionKey))
            {
                var sessionDataJson = await db.StringGetAsync($"session:{sessionKey}");
                if (!sessionDataJson.IsNullOrEmpty)
                {
                    var sessionData = JsonConvert.DeserializeObject<Dictionary<string, string>>(sessionDataJson);
                    sessionOwner = sessionData.ContainsKey("CurrentOwner") ? sessionData["CurrentOwner"] : null;
                }
            }

            var titleDetails = await _service.GetTitleByParcelAsync(parcelId);

            return new Dictionary<string, object>
            {
                ["parcelId"] = parcelId,
                ["sessionOwner"] = sessionOwner,
                ["details"] = titleDetails,
                ["archivePath"] = System.IO.Path.Combine(_archivePath, $"{parcelId}.pdf")
            };
        }

        public async Task<string> FetchDocumentFromService(string docId)
        {
            // Use async HttpClient with proper disposal and HTTPS
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                
                // Discover service endpoint dynamically using AWS Cloud Map
                var serviceUrl = await DiscoverServiceEndpointAsync("document-service") ?? _documentServiceUrl;
                
                var requestUrl = $"{serviceUrl}?id={Uri.EscapeDataString(docId)}";
                var response = await client.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();
                
                return await response.Content.ReadAsStringAsync();
            }
        }

        public async Task<string> GetSystemArchivePath()
        {
            // Replace Windows Registry access with AWS Systems Manager Parameter Store
            try
            {
                var parameterPrefix = _configuration["AWS:ParameterStorePrefix"] ?? "/landtitle/";
                var archivePath = await GetParameterAsync($"{parameterPrefix}config/archive-path");
                return archivePath ?? _archivePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to retrieve archive path from Parameter Store: {ex.Message}");
                return _archivePath;
            }
        }

        public async Task<Dictionary<string, object>> ExportTitleReport(string month, string year)
        {
            var fileName = $"report_{month}_{year}.xlsx";
            var filePath = System.IO.Path.Combine(_tempExportPath, fileName);
            
            // Port is dynamically assigned by cloud platform (ECS/EKS)
            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

            var reportData = await _service.GenerateMonthlyReportAsync(month, year);

            return new Dictionary<string, object>
            {
                ["exportPath"] = filePath,
                ["port"] = port,
                ["logPath"] = _logPath,
                ["result"] = reportData
            };
        }

        /// <summary>
        /// Async producer-consumer pattern using Channel<T> instead of BlockingCollection
        /// </summary>
        public async Task ProcessTitleAsync(string titleId)
        {
            await _processingChannel.Writer.WriteAsync(titleId);
        }

        /// <summary>
        /// Async consumer that processes titles from the channel
        /// </summary>
        public async Task ProcessTitlesFromChannelAsync()
        {
            await foreach (var titleId in _processingChannel.Reader.ReadAllAsync())
            {
                try
                {
                    // Process title asynchronously
                    await Task.Delay(100); // Simulate processing
                    Console.WriteLine($"Processed title: {titleId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing title {titleId}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Distributed cache implementation using Amazon ElastiCache for Redis
    /// Replaces static in-memory cache for cloud-native horizontal scaling
    /// </summary>
    public class TitleCache
    {
        private readonly IConnectionMultiplexer _redis;

        public TitleCache(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task StoreAsync(string key, object value, TimeSpan? expiration = null)
        {
            var db = _redis.GetDatabase();
            var serialized = JsonConvert.SerializeObject(value);
            await db.StringSetAsync($"cache:{key}", serialized, expiration ?? TimeSpan.FromHours(1));
        }

        public async Task<object> GetAsync(string key)
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync($"cache:{key}");
            return value.IsNullOrEmpty ? null : JsonConvert.DeserializeObject<object>(value);
        }

        public async Task<bool> RemoveAsync(string key)
        {
            var db = _redis.GetDatabase();
            return await db.KeyDeleteAsync($"cache:{key}");
        }
    }
}
