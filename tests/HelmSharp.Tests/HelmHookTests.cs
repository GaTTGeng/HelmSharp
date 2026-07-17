using HelmSharp.Action;

namespace HelmSharp.Tests;

public class HelmHookTests
{
    [Fact]
    public void ExtractHooks_ParsesHookAnnotation()
    {
        var manifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: my-config
              annotations:
                helm.sh/hook: pre-install,post-install
                helm.sh/hook-weight: "5"
                helm.sh/hook-delete-policy: hook-succeeded
            data:
              key: value
            """;

        var (remaining, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");

        Assert.Single(hooks);
        Assert.Contains(HelmHookEvent.PreInstall, hooks[0].Events);
        Assert.Contains(HelmHookEvent.PostInstall, hooks[0].Events);
        Assert.Equal(5, hooks[0].Weight);
        Assert.Contains(HelmHookDeletePolicy.HookSucceeded, hooks[0].DeletePolicies);
        Assert.Equal("ConfigMap", hooks[0].Kind);
        Assert.Equal("my-config", hooks[0].Name);
    }

    [Fact]
    public void ExtractHooks_SeparatesHookFromMainManifest()
    {
        var manifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: main-config
            data:
              key: value
            ---
            apiVersion: batch/v1
            kind: Job
            metadata:
              name: db-migration
              annotations:
                helm.sh/hook: pre-upgrade
            spec:
              template:
                spec:
                  containers:
                  - name: migrate
                    image: migrate:latest
                  restartPolicy: Never
            """;

        var (remaining, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");

        Assert.Single(hooks);
        Assert.Contains("main-config", remaining);
        Assert.DoesNotContain("db-migration", remaining);
        Assert.Equal("Job", hooks[0].Kind);
        Assert.Equal("db-migration", hooks[0].Name);
        Assert.Contains(HelmHookEvent.PreUpgrade, hooks[0].Events);
    }

    [Fact]
    public void ExtractHooks_DefaultDeletePolicy_IsBeforeHookCreation()
    {
        var manifest = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: test-pod
              annotations:
                helm.sh/hook: test
            spec:
              containers:
              - name: test
                image: test:latest
              restartPolicy: Never
            """;

        var (_, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");

        Assert.Single(hooks);
        Assert.Contains(HelmHookDeletePolicy.BeforeHookCreation, hooks[0].DeletePolicies);
    }

    [Fact]
    public void ExtractHooks_NoHooks_ReturnsFullManifest()
    {
        var manifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: my-config
            data:
              key: value
            """;

        var (remaining, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");

        Assert.Empty(hooks);
        Assert.Contains("my-config", remaining);
    }

    [Fact]
    public void ExtractHooks_AllHookEvents()
    {
        var events = new[]
        {
            "pre-install", "post-install", "pre-upgrade", "post-upgrade",
            "pre-delete", "post-delete", "pre-rollback", "post-rollback", "test"
        };

        foreach (var evt in events)
        {
            var manifest = $"""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: hook-{evt}
                  annotations:
                    helm.sh/hook: {evt}
                data:
                  key: value
                """;

            var (_, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");
            Assert.Single(hooks);
            Assert.Equal($"hook-{evt}", hooks[0].Name);
        }
    }

    [Fact]
    public void ExtractHooks_MultipleDeletePolicies()
    {
        var manifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: my-hook
              annotations:
                helm.sh/hook: pre-install
                helm.sh/hook-delete-policy: before-hook-creation,hook-succeeded,hook-failed
            data:
              key: value
            """;

        var (_, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");

        Assert.Single(hooks);
        Assert.Equal(3, hooks[0].DeletePolicies.Count);
        Assert.Contains(HelmHookDeletePolicy.BeforeHookCreation, hooks[0].DeletePolicies);
        Assert.Contains(HelmHookDeletePolicy.HookSucceeded, hooks[0].DeletePolicies);
        Assert.Contains(HelmHookDeletePolicy.HookFailed, hooks[0].DeletePolicies);
    }

    [Fact]
    public void ExtractHooks_HookWeight_DefaultsToZero()
    {
        var manifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: my-hook
              annotations:
                helm.sh/hook: pre-install
            data:
              key: value
            """;

        var (_, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");

        Assert.Single(hooks);
        Assert.Equal(0, hooks[0].Weight);
    }

    [Fact]
    public void ExtractHooks_MultipleDocuments_OnlyHooksExtracted()
    {
        var manifest = """
            apiVersion: v1
            kind: Service
            metadata:
              name: my-svc
            spec:
              ports:
              - port: 80
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: my-hook
              annotations:
                helm.sh/hook: post-install
            data:
              key: value
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: my-deploy
            spec:
              replicas: 1
            """;

        var (remaining, hooks) = HelmHookExecutor.ExtractHooks(manifest, "default");

        Assert.Single(hooks);
        Assert.Contains("my-svc", remaining);
        Assert.Contains("my-deploy", remaining);
        Assert.DoesNotContain("my-hook", remaining);
    }

    [Fact]
    public void ResolveStoredManifest_UsesSeparatelyStoredHooks()
    {
        var mainManifest = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: main-config
            """;
        var hookManifest = """
            apiVersion: batch/v1
            kind: Job
            metadata:
              name: delete-hook
            """;
        var record = new HelmSharp.Release.HelmReleaseRecord
        {
            Manifest = mainManifest,
            Hooks =
            [
                new HelmSharp.Release.HelmReleaseHookRecord
                {
                    Name = "delete-hook",
                    Kind = "Job",
                    Path = "templates/delete-hook.yaml",
                    Manifest = hookManifest,
                    Events = ["pre-delete", "test"],
                    Weight = -2,
                    DeletePolicies = ["hook-succeeded"]
                }
            ]
        };

        var (resolvedManifest, hooks) = HelmClient.ResolveStoredManifest(record, "default");

        Assert.Equal(mainManifest, resolvedManifest);
        var hook = Assert.Single(hooks);
        Assert.Equal("delete-hook", hook.Name);
        Assert.Contains(HelmHookEvent.PreDelete, hook.Events);
        Assert.Contains(HelmHookEvent.Test, hook.Events);
        Assert.Equal(-2, hook.Weight);
        Assert.Contains(HelmHookDeletePolicy.HookSucceeded, hook.DeletePolicies);
    }

    [Fact]
    public void ResolveStoredManifest_FallsBackToLegacyCombinedManifest()
    {
        var record = new HelmSharp.Release.HelmReleaseRecord
        {
            Manifest = """
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: main-config
                ---
                apiVersion: batch/v1
                kind: Job
                metadata:
                  name: legacy-test-hook
                  annotations:
                    helm.sh/hook: test
                spec:
                  template:
                    spec:
                      containers:
                      - name: test
                        image: test:latest
                      restartPolicy: Never
                """
        };

        var (mainManifest, hooks) = HelmClient.ResolveStoredManifest(record, "default");

        Assert.Contains("main-config", mainManifest);
        Assert.DoesNotContain("legacy-test-hook", mainManifest);
        var hook = Assert.Single(hooks);
        Assert.Equal("legacy-test-hook", hook.Name);
        Assert.Contains(HelmHookEvent.Test, hook.Events);
    }
}
