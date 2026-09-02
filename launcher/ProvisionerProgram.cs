using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using XboxAuthNet.Game.Msal;

const string ClientId = "7fcdeaa7-ba20-4883-96b0-0b68cff24bb9";
var loggerFactory = LoggerFactory.Create(config => config.ClearProviders().SetMinimumLevel(LogLevel.Warning));
var logger = loggerFactory.CreateLogger("HVMC-Provisioner");
var msal = await MsalClientHelper.BuildApplicationWithCache(ClientId);
var handler = new JELoginHandlerBuilder().WithLogger(logger).Build();

Console.WriteLine("HVMC pool-account provisioning");
Console.WriteLine("Meld ieder Microsoft-account één keer aan op deze pc.");
Console.WriteLine("Deze stap is alleen voor het beheer; jongeren krijgen daarna geen login te zien.");
Console.WriteLine();

while (true)
{
    Console.Write("Account aanmelden? [J/N]: ");
    var answer = Console.ReadLine()?.Trim().ToUpperInvariant();
    if (answer != "J") break;

    try
    {
        var account = handler.AccountManager.NewAccount();
        var authenticator = handler.CreateAuthenticator(account, default);
        authenticator.AddMsalOAuth(msal, oauth => oauth.DeviceCode(code =>
        {
            Console.WriteLine();
            Console.WriteLine(code.Message);
            Console.WriteLine();
            return Task.CompletedTask;
        }));
        authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddJEAuthenticator();
        var session = await authenticator.ExecuteForLauncherAsync();
        Console.WriteLine($"OK: {session.Username}");
        Console.WriteLine($"Local identifier: {account.Identifier}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Mislukt: {ex.Message}");
    }

    Console.WriteLine();
}

Console.WriteLine("Provisioning voltooid. Sluit dit venster.");
