using 'main.bicep'

var targetEnvironment = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')

param environmentName = targetEnvironment
param notificationDeliveryRoutes = targetEnvironment == 'test' ? [
  {
    channel: 'ContactUs'
    queue: 'notificationdeliveryevent-test-verification'
  }
] : []
param appShortName = readEnvironmentVariable('APP_SHORT_NAME', 'niobiumnotify')
param customDomainName = readEnvironmentVariable('CUSTOM_DOMAIN_NAME', '')
param serviceBusQueueNames = readEnvironmentVariable('SERVICE_BUS_QUEUE_NAMES', 'subscribecommand,notifycommand,subscribedevent,capturenotificationreceiptcommand,notificationdeliveryevent')
param storageTableNames = readEnvironmentVariable('STORAGE_TABLE_NAMES', 'Subscription,Template,NotificationSubmission,NotificationReceipt')
param isInteractiveDeployer = bool(readEnvironmentVariable('IS_INTERACTIVE_DEPLOYER', 'true'))
