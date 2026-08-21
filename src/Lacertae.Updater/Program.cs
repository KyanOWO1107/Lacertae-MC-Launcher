using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Application.Storage;
using Lacertae.Domain.Updates;
using Lacertae.Updater;

JsonSerializerOptions jsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
};

int exitCode;
try
{
    UpdaterArguments arguments = UpdaterArguments.Parse(args);
    string planJson;
    await using (Stream stream = SecureFileSystem.OpenReadExclusive(
                       arguments.PlanPath,
                       Path.GetDirectoryName(arguments.PlanPath)!))
    using (StreamReader reader = new(stream))
    {
        planJson = await reader.ReadToEndAsync();
    }
    UpdateApplyPlan? plan = JsonSerializer.Deserialize<UpdateApplyPlan>(planJson, jsonOptions);
    if (plan is null)
    {
        throw new InvalidDataException("Update plan is empty.");
    }

    UpdateApplyResult result = await new UpdateApplier().ApplyAsync(plan, CancellationToken.None);
    if (!result.Succeeded)
    {
        Console.Error.WriteLine(result.FailureCode ?? "UPDATE_APPLY_FAILED");
    }

    exitCode = result.Succeeded ? 0 : 1;
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
{
    Console.Error.WriteLine("UPDATE_ARGUMENTS_INVALID");
    exitCode = 2;
}

return exitCode;
