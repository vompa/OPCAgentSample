using Anthropic.Exceptions;

namespace OPCAgentSample.Server.Agent;

/// <summary>Fehlerabbildung für Anthropic: dieselben Arten und Texte wie im früheren Direktaufruf des SDK.</summary>
public sealed class AnthropicErrorMapper : IModelErrorMapper
{
    public ModelCallException? Map(Exception exception) => exception switch
    {
        AnthropicRateLimitException => new ModelCallException(ModelFailureKind.RateLimited, "Ratenlimit von Anthropic erreicht.", exception),
        Anthropic5xxException => new ModelCallException(ModelFailureKind.ServerError, "Anthropic meldet einen Serverfehler oder ist überlastet.", exception),
        AnthropicUnauthorizedException or AnthropicForbiddenException or AnthropicNotFoundException
            or AnthropicBadRequestException or AnthropicUnprocessableEntityException =>
            new ModelCallException(ModelFailureKind.Misconfigured, $"Anthropic lehnt die Anfrage ab ({exception.GetType().Name}).", exception),
        AnthropicIOException => new ModelCallException(ModelFailureKind.Connection, "Keine Verbindung zu Anthropic.", exception),
        AnthropicException => new ModelCallException(ModelFailureKind.Other, $"Unerwarteter Fehler beim Aufruf von Anthropic ({exception.GetType().Name}).", exception),
        HttpRequestException => new ModelCallException(ModelFailureKind.Connection, "Keine Verbindung zu Anthropic.", exception),
        _ => null,
    };
}
