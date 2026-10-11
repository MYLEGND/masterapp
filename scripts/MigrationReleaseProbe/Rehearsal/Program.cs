try
{
    if (args.Length != 1) throw new InvalidOperationException();
    if (args[0] == "contract-test") CandidateContractTests.Run();
    else if (args[0] == "migration-test")
    {
        await RehearsalFixture.Run("initialize");
        await RehearsalFixture.Run("precondition");
        await RehearsalFixture.Run("migrate");
        await RehearsalFixture.Run("verify");
        await RehearsalFixture.Run("adversarial");
    }
    else await RehearsalFixture.Run(args[0]);
}
catch
{
    // SQL/provider diagnostics can contain credentials. Emit a fixed label only.
    Console.Error.WriteLine("LEGEND_ISOLATED_FIXTURE:UNPROVEN");
    Environment.ExitCode = 1;
}

internal sealed class ProbeObservationFailure(string code) : Exception(code);
