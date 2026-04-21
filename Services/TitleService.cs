using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using log4net;

namespace LandTitleRegistration.Services
{
    public class TitleService
    {
        // VIOLATION sec-cred-001 [Security Health / Critical]: Database credentials hardcoded
        // in source code. Any developer, contractor, or CI system with repo access can connect
        // directly to the production database. Must use Azure Key Vault or AWS Secrets Manager.
        private const string DbHost     = "sql-prod.landtitle.internal";    // cr-csharp-0021
        private const string DbName     = "LandTitleDB";                    // sec-cred-001
        private const string DbUser     = "lt_admin";                       // sec-cred-001
        private const string DbPassword = "L@ndT1tle#Prod2018!";            // sec-cred-001

        // VIOLATION sec-cred-001 [Security Health / Critical]: Hardcoded API key for
        // Government Land Registry integration. Rotation is impossible without a code change.
        private const string GovApiKey  = "GLR-PROD-KEY-7f3a9b2c4d1e8f0a"; // sec-cred-001

        private static readonly ILog Log = LogManager.GetLogger(typeof(TitleService));

        private string GetConnectionString()
        {
            // VIOLATION sec-cred-001: Connection string assembled from hardcoded fields.
            return $"Server={DbHost};Database={DbName};User Id={DbUser};Password={DbPassword};"; // sec-cred-001
        }

        public Dictionary<string, object> CreateRegistration(
            string ownerName, string parcelId,
            string propertyAddress, string titleType)
        {
            var titleRef = "LT-" + DateTime.Now.Ticks.ToString().Substring(10);

            using (var conn = new SqlConnection(GetConnectionString()))
            {
                conn.Open();

                // VIOLATION sql-inject-001 [Security Health / Critical]: SQL built by string
                // concatenation. Input ownerName = "'; DROP TABLE TitleRegistrations; --" would
                // delete all registration records. Use SqlParameter for all user-supplied values.
                var sql = "INSERT INTO TitleRegistrations " +                           // sql-inject-001
                    "(TitleRef, OwnerName, ParcelId, PropertyAddress, TitleType, RegisteredDate) VALUES ('" +
                    titleRef + "', '" + ownerName + "', '" + parcelId +                 // sql-inject-001
                    "', '" + propertyAddress + "', '" + titleType + "', GETDATE())";    // sql-inject-001

                using (var cmd = new SqlCommand(sql, conn))
                    cmd.ExecuteNonQuery();
            }

            // VIOLATION sec-weak-hash [Security Health / High]: SHA1 is cryptographically
            // broken (SHATTERED attack, 2017). Do not use for any security-sensitive hashing.
            // Use SHA-256 or BCrypt for confirmation codes.
            string confirmCode = ComputeSha1Hash(titleRef + ownerName);                 // sec-weak-hash

            var result = new Dictionary<string, object>
            {
                ["titleRef"]      = titleRef,
                ["ownerName"]     = ownerName,
                ["parcelId"]      = parcelId,
                ["address"]       = propertyAddress,
                ["type"]          = titleType,
                ["confirmation"]  = confirmCode,
                ["dbHost"]        = DbHost                                              // cr-csharp-0021
            };
            Log.Info("Registration created: " + titleRef);
            return result;
        }

        public Dictionary<string, object> GetTitleByParcel(string parcelId)
        {
            // VIOLATION sql-inject-001 [Security Health / Critical]: parcelId is user-supplied
            // and appended directly into SQL. Parameterise with SqlParameter("@parcelId", parcelId).
            var sql = "SELECT * FROM TitleRegistrations WHERE ParcelId = '" + parcelId + "'"; // sql-inject-001
            var result = new Dictionary<string, object>();
            using (var conn = new SqlConnection(GetConnectionString()))
            {
                conn.Open();
                using (var cmd = new SqlCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            result[reader.GetName(i)] = reader.GetValue(i)?.ToString() ?? string.Empty;
                    }
                }
            }
            return result;
        }

        // VIOLATION complexity-001 [Code Sustainability / High]: Cyclomatic complexity > 10.
        // 11 conditional branches in one method — high maintenance cost and transformation risk.
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

        // VIOLATION dup-logic-001 [Code Sustainability / Medium]: Title type validation
        // duplicated — identical check already exists inside CalculateRegistrationFee.
        // Extract to a shared private ValidateTitleType() method or a TitleType enum.
        public bool IsTitleTypeValid(string titleType)                                  // dup-logic-001
        {
            return titleType == "FREEHOLD"   || titleType == "LEASEHOLD" ||             // dup-logic-001
                   titleType == "COMMONHOLD" || titleType == "ABSOLUTE";                // dup-logic-001
        }

        public string GenerateMonthlyReport(string month, string year)
        {
            // VIOLATION sec-cred-001: GovApiKey transmitted as plain query string —
            // visible in server logs, browser history, and HTTP proxies.
            var url = $"http://gov.landregistry.internal/reports?month={month}" +      // cr-csharp-0088
                      $"&year={year}&apiKey={GovApiKey}";                               // sec-cred-001, cr-csharp-0088
            return $"Report requested via: {url}";
        }

        // Missing XML doc comment — flagged by Code Sustainability rules
        public List<string> SearchByOwner(string ownerName)                            // doc-missing-001
        {
            // VIOLATION sql-inject-001: LIKE query with unparameterised user input.
            var sql = "SELECT TitleRef FROM TitleRegistrations WHERE OwnerName LIKE '%" // sql-inject-001
                      + ownerName + "%'";                                               // sql-inject-001
            var refs = new List<string>();
            using (var conn = new SqlConnection(GetConnectionString()))
            {
                conn.Open();
                using (var cmd = new SqlCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read()) refs.Add(reader.GetString(0));
            }
            return refs;
        }

        private string ComputeSha1Hash(string input)                                   // sec-weak-hash
        {
            // Updated to use SHA1.Create() for .NET 8 compatibility
            using (var sha1 = SHA1.Create())                                           // sec-weak-hash
            {
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));            // sec-weak-hash
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
