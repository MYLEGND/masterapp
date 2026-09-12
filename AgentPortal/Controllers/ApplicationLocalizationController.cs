using Domain.Messaging;
using Infrastructure.Messaging;
using Shared.Messaging;

namespace AgentPortal.Controllers;

public sealed class ApplicationLocalizationController(
    IMessagingActorContextResolver actors,
    IApplicationLocalizationService localization)
    : ApplicationLocalizationControllerBase(actors, localization);
