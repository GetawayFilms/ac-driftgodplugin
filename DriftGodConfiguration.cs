using FluentValidation;
using AssettoServer.Server.Configuration;

namespace DriftGodPlugin;

public class DriftGodConfiguration : IValidateConfiguration<DriftGodConfigurationValidator>
{
    public bool EnableServerAnnouncements { get; init; } = true;
    public bool SavePlayerRecords { get; init; } = true;
    public int LeaderboardSize { get; init; } = 10;
    public bool BroadcastPersonalBests { get; init; } = true;
}

public class DriftGodConfigurationValidator : AbstractValidator<DriftGodConfiguration>
{
    public DriftGodConfigurationValidator()
    {
        RuleFor(x => x.LeaderboardSize)
            .InclusiveBetween(1, 100)
            .WithMessage("LeaderboardSize must be between 1 and 100");
    }
}