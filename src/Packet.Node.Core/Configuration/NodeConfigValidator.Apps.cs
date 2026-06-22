using FluentValidation;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// Validates one <see cref="ApplicationConfig"/>: a stable id, a launch verb that does not
/// collide with a built-in console verb, and the fields its <see cref="ApplicationKind"/>
/// requires (a process app needs a command). The built-in-verb collision is checked by
/// running the verb through <see cref="NodeCommandParser"/> — the single source of truth for
/// what the console already understands — and rejecting anything it classifies as a real
/// command (so a registered app can never be dead config, shadowed by a built-in).
/// </summary>
public sealed class ApplicationConfigValidator : AbstractValidator<ApplicationConfig>
{
    public ApplicationConfigValidator()
    {
        RuleFor(a => a.Id)
            .NotEmpty().WithMessage("application.id is required.");

        RuleFor(a => a.Command)
            .NotEmpty().WithMessage("application.command (the launch verb) is required.")
            .Must(NotABuiltInVerb)
            .WithMessage(a => $"application.command '{a.Command}' collides with a built-in console verb " +
                "(CONNECT/BYE/NODES/INFO/HELP/SYSOP/SESSIONS/KICK/PORT/RELOAD or an abbreviation) — pick another.");

        RuleFor(a => a.Executable)
            .NotEmpty().WithMessage("application.executable is required for a process application.")
            .When(a => a.Kind == ApplicationKind.Process);

        RuleFor(a => a.SocketPath)
            .NotEmpty().WithMessage("application.socketPath is required for a socket application.")
            .When(a => a.Kind == ApplicationKind.Socket);

        // When a ui block is present, its upstream must be an absolute http(s) URL — pdn
        // reverse-proxies to it, so anything else is unusable config.
        When(a => a.Ui is not null, () =>
            RuleFor(a => a.Ui!.Upstream)
                .Must(BeAnAbsoluteHttpUrl)
                .WithMessage(a => $"application.ui.upstream '{a.Ui!.Upstream}' must be an absolute http(s) URL (e.g. http://127.0.0.1:9090)."));
    }

    private static bool BeAnAbsoluteHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // The verb is safe iff the parser does NOT recognise it as a command — i.e. it falls
    // through to Unknown (or is empty). Anything that parses to a real verb (or a malformed
    // form of one, e.g. a bare "C") would be intercepted by the console before the app could
    // ever launch, so reject it at config time rather than ship dead config.
    private static bool NotABuiltInVerb(string? match)
    {
        if (string.IsNullOrWhiteSpace(match))
        {
            return true;   // emptiness is reported by the NotEmpty rule above.
        }
        var parsed = NodeCommandParser.Parse(match.Trim());
        return parsed is UnknownCommand or EmptyCommand;
    }
}

/// <summary>
/// Validates one <see cref="AppOverrideConfig"/> (an <c>apps:</c> package-override entry).
/// Only the id is constrained here — Enabled/Match/Environment are free-form overrides whose
/// meaning the catalog resolves against the discovered manifest.
/// </summary>
public sealed class AppOverrideConfigValidator : AbstractValidator<AppOverrideConfig>
{
    public AppOverrideConfigValidator()
    {
        RuleFor(a => a.Id)
            .Must(id => !string.IsNullOrWhiteSpace(id))
            .WithMessage("apps entry id is required (the package id it applies to).");
    }
}
