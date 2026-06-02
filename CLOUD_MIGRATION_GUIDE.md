# Cloud Migration Guide - Land Title Registration Application

## Overview
This application has been modernized for AWS cloud deployment with cloud-native patterns and best practices.

## Cloud Readiness Fixes Applied

### 1. Configuration Management (Critical)

#### Hard-coded Service URLs (cr-dotnet-0011)
- **Before**: Service URLs hardcoded in source code
- **After**: URLs loaded from AWS Systems Manager Parameter Store
- **Parameters Required**:
  - `/landtitle/service-urls/document-service`
  - `/landtitle/service-urls/notification-service`
  - `/landtitle/service-urls/legacy-search-api`
  - `/landtitle/service-urls/gov-registry`

#### Hard-coded Connection Strings (cr-dotnet-0009)
- **Before**: Database credentials hardcoded in source code
- **After**: Connection strings loaded from AWS Secrets Manager
- **Secrets Required**:
  - `landtitle/database/connection-string`

#### Hard-coded Secrets (cr-dotnet-0123)
- **Before**: API keys embedded in source code
- **After**: API keys loaded from AWS Secrets Manager
- **Secrets Required**:
  - `landtitle/api-keys/government-registry`

### 2. File System & Storage (High)

#### Hard-coded File Paths (cr-dotnet-0001)
- **Before**: Windows-specific absolute paths (C:\, D:\)
- **After**: Cross-platform paths using Path.Combine() and environment variables
- **Environment Variables**:
  - `ARCHIVE_PATH`: Archive directory path
  - `TEMP_EXPORT_PATH`: Temporary export directory
  - `LOG_PATH`: Log file path

### 3. Networking & Communication (Critical)

#### Hard-coded Port Numbers (cr-dotnet-0017)
- **Before**: Fixed port 8080 hardcoded
- **After**: Port loaded from environment variable `PORT`
- **Service Discovery**: AWS Cloud Map integration for dynamic endpoint resolution

### 4. State Management & Session (High)

#### In-Process Session State (cr-dotnet-0045, cr-dotnet-0126)
- **Before**: HttpSessionState (InProc) - instance-local, lost on restart
- **After**: Amazon ElastiCache for Redis - distributed, persistent
- **Configuration Required**:
  - Redis connection string in `appsettings.json` or environment variable `REDIS_CONNECTION_STRING`

#### Static Collections (cr-dotnet-0006)
- **Before**: Static Dictionary for caching - instance-local, unbounded growth
- **After**: Redis distributed cache with TTL and expiration

### 5. Platform-Specific Dependencies (High)

#### Windows Registry Access (cr-dotnet-0040)
- **Before**: Microsoft.Win32.Registry - Windows-only
- **After**: AWS Systems Manager Parameter Store - cross-platform
- **Parameter**: `/landtitle/config/archive-path`

#### IIS Module Dependencies (cr-dotnet-0044)
- **Before**: IIS-specific modules and HttpContext
- **After**: ASP.NET Core middleware-compatible patterns

### 6. Logging & Monitoring (High)

#### Log4net File Appenders (cr-dotnet-0035)
- **Before**: RollingFileAppender writing to local disk
- **After**: AWS CloudWatch Logs appender
- **Configuration**: See `log4net.config`

### 7. Time & Clock Dependencies (High)

#### DateTime.Now Usage (cr-dotnet-0121)
- **Before**: DateTime.Now - server-local timezone
- **After**: DateTimeOffset.UtcNow - UTC timestamps
- **Benefit**: Consistent timestamps across regions

### 8. Resource Management (Low)

#### Synchronous HttpClient (cr-dotnet-0037)
- **Before**: .GetAwaiter().GetResult() - blocking calls
- **After**: async/await pattern throughout

#### Blocking Collections (cr-dotnet-0039)
- **Before**: BlockingCollection<T>.Take() - blocking operations
- **After**: System.Threading.Channels with async enumeration

## AWS Services Required

### 1. AWS Systems Manager Parameter Store
Store configuration parameters:
```bash
aws ssm put-parameter --name "/landtitle/service-urls/document-service" \
  --value "https://docs.landtitle.internal:8090/fetch" --type String

aws ssm put-parameter --name "/landtitle/service-urls/notification-service" \
  --value "https://notify.landtitle.internal:7070/send" --type String

aws ssm put-parameter --name "/landtitle/service-urls/legacy-search-api" \
  --value "https://search.landtitle.internal:9191/search/titles" --type String

aws ssm put-parameter --name "/landtitle/config/archive-path" \
  --value "/mnt/efs/archives" --type String
```

### 2. AWS Secrets Manager
Store sensitive credentials:
```bash
aws secretsmanager create-secret --name "landtitle/database/connection-string" \
  --secret-string "Server=landtitle-db.cluster-xxx.us-east-1.rds.amazonaws.com;Database=LandTitleDB;User Id=admin;Password=xxx;"

aws secretsmanager create-secret --name "landtitle/api-keys/government-registry" \
  --secret-string "GLR-PROD-KEY-xxx"
```

### 3. Amazon ElastiCache for Redis
Create Redis cluster for distributed caching and session state:
```bash
aws elasticache create-cache-cluster \
  --cache-cluster-id landtitle-redis \
  --engine redis \
  --cache-node-type cache.t3.micro \
  --num-cache-nodes 1
```

Update `appsettings.json` with Redis connection string:
```json
{
  "Redis": {
    "ConnectionString": "landtitle-redis.xxx.cache.amazonaws.com:6379"
  }
}
```

### 4. AWS Cloud Map (Optional)
Register services for dynamic discovery:
```bash
aws servicediscovery create-private-dns-namespace \
  --name landtitle.local \
  --vpc vpc-xxx

aws servicediscovery create-service \
  --name document-service \
  --namespace-id ns-xxx \
  --dns-config "NamespaceId=ns-xxx,DnsRecords=[{Type=A,TTL=60}]"
```

### 5. Amazon CloudWatch Logs
Log group is automatically created by the application:
- Log Group: `/aws/landtitle/application`
- Region: `us-east-1` (configurable)

## Environment Variables

Set these environment variables for the application:

```bash
# AWS Configuration
AWS_REGION=us-east-1
AWS_DEFAULT_REGION=us-east-1

# File Paths (optional, defaults to relative paths)
ARCHIVE_PATH=/mnt/efs/archives
TEMP_EXPORT_PATH=/mnt/efs/temp/exports
LOG_PATH=/var/log/landtitle/registration.log

# Port (dynamically assigned by ECS/EKS)
PORT=8080

# Redis Connection (if not in appsettings.json)
REDIS_CONNECTION_STRING=landtitle-redis.xxx.cache.amazonaws.com:6379

# Database Connection (fallback if Secrets Manager unavailable)
DATABASE_CONNECTION_STRING=Server=xxx;Database=LandTitleDB;...

# Government API Key (fallback if Secrets Manager unavailable)
GOV_API_KEY=GLR-PROD-KEY-xxx
```

## IAM Permissions Required

The application requires the following IAM permissions:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "ssm:GetParameter",
        "ssm:GetParameters",
        "ssm:GetParametersByPath"
      ],
      "Resource": "arn:aws:ssm:*:*:parameter/landtitle/*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "secretsmanager:GetSecretValue"
      ],
      "Resource": "arn:aws:secretsmanager:*:*:secret:landtitle/*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "logs:CreateLogGroup",
        "logs:CreateLogStream",
        "logs:PutLogEvents"
      ],
      "Resource": "arn:aws:logs:*:*:log-group:/aws/landtitle/*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "servicediscovery:DiscoverInstances"
      ],
      "Resource": "*"
    }
  ]
}
```

## Deployment Checklist

- [ ] Create AWS Systems Manager parameters
- [ ] Create AWS Secrets Manager secrets
- [ ] Provision Amazon ElastiCache for Redis cluster
- [ ] Configure IAM role with required permissions
- [ ] Set environment variables
- [ ] Mount EFS volumes for persistent storage (if needed)
- [ ] Configure CloudWatch Logs log group
- [ ] Test service discovery (if using AWS Cloud Map)
- [ ] Verify Redis connectivity
- [ ] Test database connectivity
- [ ] Validate secret retrieval

## Migration Benefits

1. **Portability**: No hardcoded paths, URLs, or platform-specific code
2. **Security**: Credentials stored in AWS Secrets Manager with encryption and rotation
3. **Scalability**: Distributed session state and caching enable horizontal scaling
4. **Observability**: Centralized logging in CloudWatch Logs
5. **Resilience**: Configuration changes without code deployment
6. **12-Factor Compliance**: Externalized configuration, stateless processes, port binding

## Testing

### Local Development
1. Set environment variables or use `appsettings.json`
2. Run local Redis instance: `docker run -p 6379:6379 redis`
3. Configure local database connection
4. Test without AWS services using fallback configuration

### AWS Deployment
1. Deploy to ECS/EKS with IAM role
2. Verify Parameter Store and Secrets Manager access
3. Test Redis connectivity
4. Monitor CloudWatch Logs
5. Validate service discovery (if enabled)

## Troubleshooting

### Cannot connect to Redis
- Verify security group allows inbound traffic on port 6379
- Check Redis connection string in configuration
- Ensure Redis cluster is in same VPC

### Cannot retrieve secrets
- Verify IAM role has `secretsmanager:GetSecretValue` permission
- Check secret name matches configuration
- Ensure secrets exist in correct region

### Cannot retrieve parameters
- Verify IAM role has `ssm:GetParameter` permission
- Check parameter names match configuration
- Ensure parameters exist in correct region

### Logs not appearing in CloudWatch
- Verify IAM role has CloudWatch Logs permissions
- Check log group name in configuration
- Ensure AWS region is correctly configured

## Support

For issues or questions, contact the cloud migration team.
