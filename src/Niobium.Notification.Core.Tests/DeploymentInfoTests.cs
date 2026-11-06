using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Niobium.Notification.Server;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class DeploymentInfoTests
{
    [TestMethod]
    public void Health_ExposesOnlyReadinessAndCompiledCommit()
    {
        OkObjectResult result = (OkObjectResult)new DeploymentInfo().Run(new DefaultHttpContext().Request);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.AreEqual(200, result.StatusCode);
        CollectionAssert.AreEquivalent(new[] { "status", "commit" }, json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("ready", json.RootElement.GetProperty("status").GetString());
        string? commit = typeof(DeploymentInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "Commit").Value;
        Assert.AreEqual(commit, json.RootElement.GetProperty("commit").GetString());
    }

    [TestMethod]
    public void Health_BindsOnlyAnonymousGet()
    {
        MethodInfo method = typeof(DeploymentInfo).GetMethod(nameof(DeploymentInfo.Run))!;
        Assert.AreEqual(nameof(DeploymentInfo), method.GetCustomAttribute<FunctionAttribute>()!.Name);
        HttpTriggerAttribute trigger = method.GetParameters()[0].GetCustomAttribute<HttpTriggerAttribute>()!;
        Assert.AreEqual(AuthorizationLevel.Anonymous, trigger.AuthLevel);
        Assert.AreEqual("health", trigger.Route);
        CollectionAssert.AreEqual(new[] { "get" }, trigger.Methods);
    }
}
