namespace ProtectWebsite.Services.MetaSignal;

public sealed class MetaSignalIntelligenceOptions
{
    public bool Enabled { get; set; } = true;
    public bool SendBrowserEvents { get; set; } = true;
    public bool SendServerEvents { get; set; } = true;
    public bool PersistEvents { get; set; } = true;
    public bool AnalyticsBridgeEnabled { get; set; } = true;
    public int AnalyticsBridgePollSeconds { get; set; } = 45;
    public int AnalyticsBridgeBatchSize { get; set; } = 100;
    public int AnalyticsBridgeStartupLookbackHours { get; set; } = 24;
    public bool DebugMode { get; set; }
    public int HighIntentThreshold { get; set; } = 70;
    public int LeadReadyThreshold { get; set; } = 90;
    public MetaSignalScoreWeights Weights { get; set; } = new();
}

public sealed class MetaSignalScoreWeights
{
    public int LandingViewed { get; set; } = 5;
    public int Stay5Seconds { get; set; } = 5;
    public int Stay15Seconds { get; set; } = 10;
    public int MeaningfulScroll { get; set; } = 5;
    public int FirstQuestionAnswered { get; set; } = 15;
    public int Step1Completed { get; set; } = 20;
    public int Step2Completed { get; set; } = 25;
    public int RecommendationViewed { get; set; } = 30;
    public int ContactStepReached { get; set; } = 35;
    public int ContactInputStarted { get; set; } = 20;
    public int PhoneCompleted { get; set; } = 25;
    public int RequiredContactCompleted { get; set; } = 35;
    public int SubmitAttempted { get; set; } = 40;
    public int SuccessfulLeadSubmitted { get; set; } = 100;
    public int ProtectingJustMe { get; set; } = 6;
    public int ProtectingSpouseOrPartner { get; set; } = 12;
    public int ProtectingChildren { get; set; } = 14;
    public int ProtectingFamily { get; set; } = 16;
    public int ProtectingNotSure { get; set; } = 8;
    public int GoalReplaceIncome { get; set; } = 16;
    public int GoalFinalExpenses { get; set; } = 12;
    public int GoalMortgageOrBills { get; set; } = 15;
    public int GoalLeaveSomething { get; set; } = 14;
    public int GoalNotSure { get; set; } = 7;
    public int Age18To24 { get; set; } = 6;
    public int Age25To34 { get; set; } = 8;
    public int Age35To44 { get; set; } = 10;
    public int Age45To54 { get; set; } = 9;
    public int Age55Plus { get; set; } = 7;
    public int RapidBounce { get; set; } = -15;
    public int FieldError { get; set; } = -4;
    public int ContactFriction { get; set; } = -8;
    public int Backtrack { get; set; } = -3;
    public int DeadClick { get; set; } = -3;
    public int RageClick { get; set; } = -5;
    public int HighIntentAbandon { get; set; } = -12;
    public int ContactStepAbandon { get; set; } = -8;
}
