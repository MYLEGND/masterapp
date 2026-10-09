try
{
    if (args.Length != 1) throw new InvalidOperationException();
    await RehearsalFixture.Run(args[0]);
}
catch
{
    // SQL/provider diagnostics can contain credentials. Emit a fixed label only.
    Console.Error.WriteLine("LEGEND_ISOLATED_FIXTURE:UNPROVEN");
    Environment.ExitCode = 1;
}

internal sealed class ProbeObservationFailure(string code) : Exception(code);
