using ParallelSearch.Cli;

var application = new CliApplication(
    baseAddress => new HttpClient
    {
        BaseAddress = baseAddress
    },
    Console.Out,
    Console.Error);

return await application.RunAsync(args);
