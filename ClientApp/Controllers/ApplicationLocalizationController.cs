using Domain.Messaging;
using Infrastructure.Messaging;
using Shared.Messaging;

namespace ClientApp.Controllers;

public sealed class ApplicationLocalizationController(
    IMessagingActorContextResolver actors,
    IApplicationLocalizationService localization)
    : ApplicationLocalizationControllerBase(actors, localization);
