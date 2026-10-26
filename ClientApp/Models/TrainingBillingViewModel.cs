using Domain.Entities;
namespace ClientApp.Models;
public sealed record TrainingBillingViewModel(ClientSubscription Subscription, ClientSubscriptionOffer Offer,
    IReadOnlyList<ClientPaymentMethod> PaymentMethods, IReadOnlyList<SubscriptionPayment> Payments,
    string? ApplicationId, string? LocationId, string Environment);
