namespace Domain.Billing;

public enum BillingProvider
{
    Square = 0
}

public enum BillingProviderEnvironment
{
    Sandbox = 0,
    Production = 1
}

public enum ClientSubscriptionOfferPriceType
{
    Fixed50 = 0,
    Fixed75 = 1,
    Fixed100 = 2,
    Fixed150 = 3,
    Custom = 4
}

public enum BillingAnchorSelectionMode
{
    FirstOfMonth = 1,
    FifteenthOfMonth = 2,
    SpecificDayOfMonth = 3
}

public enum ClientSubscriptionOfferStatus
{
    Draft = 0,
    Offered = 1,
    Accepted = 2,
    Superseded = 3,
    Expired = 4,
    Revoked = 5
}

public enum SubscriptionActivationInvitationStatus
{
    Pending = 0,
    Sent = 1,
    Viewed = 2,
    PaymentStarted = 3,
    Redeemed = 4,
    Expired = 5,
    Revoked = 6,
    Superseded = 7
}

public enum ClientSubscriptionStatus
{
    Draft = 0,
    AwaitingPaymentMethod = 1,
    PendingProviderActivation = 2,
    Active = 3,
    PastDue = 4,
    GracePeriod = 5,
    Suspended = 6,
    Canceled = 7,
    Paused = 8,
    ActivationFailed = 9,
    ReconciliationRequired = 10
}

public enum ClientSubscriptionPaymentStanding
{
    Unknown = 0,
    Current = 1,
    RequiresAction = 2,
    PastDue = 3,
    GracePeriod = 4,
    Failed = 5
}

public enum SubscriptionPaymentStatus
{
    Pending = 0,
    Processing = 1,
    Authorized = 2,
    Completed = 3,
    Failed = 4,
    Canceled = 5,
    PartiallyRefunded = 6,
    Refunded = 7,
    Disputed = 8
}

public enum SubscriptionPaymentKind
{
    CommerceOneTime = 0,
    InitialActivation = 1,
    Renewal = 2
}

public enum BillingProviderEventProcessingStatus
{
    Received = 0,
    Validated = 1,
    Processing = 2,
    Processed = 3,
    Failed = 4,
    Deferred = 5,
    IgnoredUnsupported = 6,
    ReconciliationRequired = 7
}

public enum ClientEntitlementStatus
{
    NotGranted = 0,
    Active = 1,
    GracePeriod = 2,
    Restricted = 3,
    Suspended = 4,
    Revoked = 5
}

public enum ClientEntitlementSourceType
{
    Subscription = 0,
    Payment = 1,
    Manual = 2,
    Reconciliation = 3
}

public enum BillingActorType
{
    System = 0,
    Agent = 1,
    Client = 2,
    Provider = 3,
    Webhook = 4,
    Reconciliation = 5
}

public enum ClientIdentityContinuationPurpose
{
    Activation = 0,
    SignIn = 1
}
