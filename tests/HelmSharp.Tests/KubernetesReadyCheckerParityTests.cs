using System.Net;
using System.Text.Json;

namespace HelmSharp.Tests;

public sealed class KubernetesReadyCheckerParityTests
{
    [Theory]
    [MemberData(nameof(ReadinessCases))]
    public async Task WaitForReadyAsync_UsesHelmCompatibleWorkloadPredicates(ReadinessCase testCase)
    {
        var handler = new KubernetesApiHandler()
            .RespondAlways(HttpMethod.Get, testCase.ResourcePath, HttpStatusCode.OK, testCase.ResourceJson);
        if (testCase.AuxiliaryPath is not null)
        {
            handler.RespondAlways(
                HttpMethod.Get,
                testCase.AuxiliaryPath,
                HttpStatusCode.OK,
                testCase.AuxiliaryJson!);
        }

        var timeProvider = new DeterministicTimeProvider(DateTimeOffset.UnixEpoch);
        var polling = new DeterministicPolling(timeProvider);
        var waiter = new HelmSharp.Kube.KubernetesResourceWaiter(
            KubernetesTestClientBuilder.Create(handler),
            timeoutSeconds: 1,
            timeProvider,
            polling.DelayAsync);
        var manifest = $"apiVersion: {testCase.ApiVersion}\nkind: {testCase.Kind}\nmetadata:\n  name: demo\n";

        if (testCase.Outcome == ReadinessOutcome.Ready)
        {
            var messages = await AsyncEnumerableTestExtensions.CollectAsync(
                waiter.WaitForReadyAsync(manifest, "ns"));
            Assert.Contains("All 1 resources are ready", messages);
        }
        else if (testCase.Outcome == ReadinessOutcome.Pending)
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForReadyAsync(manifest, "ns")));
            Assert.Contains(testCase.Kind, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AsyncEnumerableTestExtensions.DrainAsync(waiter.WaitForReadyAsync(manifest, "ns")));
            Assert.Contains("demo", exception.Message);
        }
    }

    public static IEnumerable<object[]> ReadinessCases()
    {
        // Helm v3.17.3 ReadyChecker reference: https://github.com/helm/helm/blob/v3.17.3/pkg/kube/ready.go
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("1", 4), ("2", 3)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(paused: true),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 4)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(observedGeneration: 0),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 4)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("1", 4), ("2", 2)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(exceededProgressDeadline: true),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 2)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(maxUnavailable: "50%"),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 2)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(strategyType: "Recreate"),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 3)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(strategyType: "Recreate"),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 4)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", DuplicateTemplateReplicaSetList(), ReadinessOutcome.Ready)];
        yield return [CreateCase("DaemonSet", "apps/v1", "/apis/apps/v1/namespaces/ns/daemonsets/demo", DaemonSet(4, 4, 3, numberAvailable: 0),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("DaemonSet", "apps/v1", "/apis/apps/v1/namespaces/ns/daemonsets/demo", DaemonSet(4, 4, 2, maxUnavailable: "50%"),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("DaemonSet", "apps/v1", "/apis/apps/v1/namespaces/ns/daemonsets/demo", DaemonSet(4, 4, 0, maxUnavailable: "invalid"),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("DaemonSet", "apps/v1", "/apis/apps/v1/namespaces/ns/daemonsets/demo", DaemonSet(4, 3, 4),
            outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("DaemonSet", "apps/v1", "/apis/apps/v1/namespaces/ns/daemonsets/demo", DaemonSet(4, 0, 0, strategy: "OnDelete"),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("StatefulSet", "apps/v1", "/apis/apps/v1/namespaces/ns/statefulsets/demo", StatefulSet(5, 5, 3, partition: 2),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("StatefulSet", "apps/v1", "/apis/apps/v1/namespaces/ns/statefulsets/demo", StatefulSet(5, 5, 2, partition: 2),
            outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("StatefulSet", "apps/v1", "/apis/apps/v1/namespaces/ns/statefulsets/demo", StatefulSet(3, 3, 3, currentRevision: "old", updateRevision: "new"),
            outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("StatefulSet", "apps/v1", "/apis/apps/v1/namespaces/ns/statefulsets/demo", StatefulSet(3, 0, 0, strategy: "OnDelete"),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("ReplicaSet", "apps/v1", "/apis/apps/v1/namespaces/ns/replicasets/demo", ReplicaSet(),
            "/api/v1/namespaces/ns/pods", PodList(ready: true), ReadinessOutcome.Ready)];
        yield return [CreateCase("ReplicaSet", "apps/v1", "/apis/apps/v1/namespaces/ns/replicasets/demo", ReplicaSet(observedGeneration: 0),
            "/api/v1/namespaces/ns/pods", PodList(ready: true), ReadinessOutcome.Pending)];
        yield return [CreateCase("ReplicaSet", "apps/v1", "/apis/apps/v1/namespaces/ns/replicasets/demo", ReplicaSet(2),
            "/api/v1/namespaces/ns/pods", PodList(ready: false), ReadinessOutcome.Pending)];
        yield return [CreateCase("ReplicationController", "v1", "/api/v1/namespaces/ns/replicationcontrollers/demo", ReplicationController(),
            "/api/v1/namespaces/ns/pods", PodList(ready: true), ReadinessOutcome.Ready)];
        yield return [CreateCase("ReplicationController", "v1", "/api/v1/namespaces/ns/replicationcontrollers/demo", ReplicationController(observedGeneration: 0),
            "/api/v1/namespaces/ns/pods", PodList(ready: true), ReadinessOutcome.Pending)];
        yield return [CreateCase("ReplicationController", "v1", "/api/v1/namespaces/ns/replicationcontrollers/demo", ReplicationController(),
            "/api/v1/namespaces/ns/pods", PodList(ready: false), ReadinessOutcome.Pending)];
        yield return [CreateCase("Pod", "v1", "/api/v1/namespaces/ns/pods/demo", Pod("Running", ready: true), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Pod", "v1", "/api/v1/namespaces/ns/pods/demo", Pod("Succeeded", ready: true), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Pod", "v1", "/api/v1/namespaces/ns/pods/demo", Pod("Pending", ready: false), outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("Pod", "v1", "/api/v1/namespaces/ns/pods/demo", Pod("Failed", ready: false), outcome: ReadinessOutcome.Failed)];
        yield return [CreateCase("PersistentVolumeClaim", "v1", "/api/v1/namespaces/ns/persistentvolumeclaims/demo", Pvc("Bound"), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("PersistentVolumeClaim", "v1", "/api/v1/namespaces/ns/persistentvolumeclaims/demo", Pvc("Pending"), outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("PersistentVolumeClaim", "v1", "/api/v1/namespaces/ns/persistentvolumeclaims/demo", Pvc("Lost"), outcome: ReadinessOutcome.Failed)];
        yield return [CreateCase("Endpoints", "v1", "/api/v1/namespaces/ns/endpoints/demo", Endpoints(ready: true), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Endpoints", "v1", "/api/v1/namespaces/ns/endpoints/demo", Endpoints(ready: false), outcome: ReadinessOutcome.Pending)];
    }

    private static ReadinessCase CreateCase(
        string kind,
        string apiVersion,
        string resourcePath,
        string resourceJson,
        string? auxiliaryPath = null,
        string? auxiliaryJson = null,
        ReadinessOutcome outcome = ReadinessOutcome.Ready)
        => new(kind, apiVersion, resourcePath, resourceJson, auxiliaryPath, auxiliaryJson, outcome);

    private static string Deployment(int observedGeneration = 1, bool paused = false, string maxUnavailable = "25%",
        bool exceededProgressDeadline = false, string strategyType = "RollingUpdate")
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "Deployment",
            metadata = new { name = "demo", uid = "deployment-uid", generation = 1 },
            spec = new
            {
                replicas = 4,
                paused,
                selector = new { matchLabels = new Dictionary<string, string> { ["app"] = "demo" } },
                strategy = new
                {
                    type = strategyType,
                    rollingUpdate = strategyType == "RollingUpdate" ? new { maxUnavailable } : null
                },
                template = PodTemplate("desired", includeHash: false, hash: "")
            },
            status = new
            {
                observedGeneration,
                conditions = exceededProgressDeadline
                    ? new[] { new { type = "Progressing", status = "False", reason = "ProgressDeadlineExceeded" } }
                    : []
            }
        });

    private static string ReplicaSetList(params (string Revision, int Ready)[] replicas)
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "ReplicaSetList",
            items = replicas.Select(replica => new
            {
                metadata = new
                {
                    name = $"rs-{replica.Revision}",
                    uid = $"rs-{replica.Revision}",
                    generation = 1,
                    creationTimestamp = replica.Revision == "1" ? "2024-01-01T00:00:00Z" : "2024-02-01T00:00:00Z",
                    ownerReferences = new[] { new { apiVersion = "apps/v1", kind = "Deployment", uid = "deployment-uid" } },
                    annotations = new Dictionary<string, string> { ["deployment.kubernetes.io/revision"] = replica.Revision }
                },
                spec = new { template = PodTemplate(replica.Revision == "1" ? "previous" : "desired", includeHash: true, replica.Revision) },
                status = new { observedGeneration = 1, readyReplicas = replica.Ready }
            })
        });

    private static string DuplicateTemplateReplicaSetList()
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "ReplicaSetList",
            items = new[]
            {
                ReplicaSetResource("9", 4, "different", "2020-01-01T00:00:00Z"),
                ReplicaSetResource("2", 3, "desired", "2021-01-01T00:00:00Z"),
                ReplicaSetResource("3", 0, "desired", "2022-01-01T00:00:00Z")
            }
        });

    private static object ReplicaSetResource(string revision, int ready, string image, string createdAt)
        => new
        {
            metadata = new
            {
                name = $"rs-{revision}",
                uid = $"rs-{revision}",
                generation = 1,
                creationTimestamp = createdAt,
                ownerReferences = new[] { new { apiVersion = "apps/v1", kind = "Deployment", uid = "deployment-uid" } },
                annotations = new Dictionary<string, string> { ["deployment.kubernetes.io/revision"] = revision }
            },
            spec = new { template = PodTemplate(image, includeHash: true, revision) },
            status = new { observedGeneration = 1, readyReplicas = ready }
        };

    private static object PodTemplate(string image, bool includeHash, string hash)
    {
        var labels = new Dictionary<string, string> { ["app"] = "demo" };
        if (includeHash)
            labels["pod-template-hash"] = hash;

        return new
        {
            metadata = new { labels },
            spec = new { containers = new[] { new { name = "app", image } } }
        };
    }

    private static string DaemonSet(int desired, int updated, int ready, string strategy = "RollingUpdate", string maxUnavailable = "1", int? numberAvailable = null)
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "DaemonSet",
            metadata = new { name = "demo", generation = 1 },
            spec = new { updateStrategy = new { type = strategy, rollingUpdate = new { maxUnavailable } } },
            status = new { observedGeneration = 1, desiredNumberScheduled = desired, updatedNumberScheduled = updated, numberReady = ready, numberAvailable = numberAvailable ?? ready }
        });

    private static string StatefulSet(int replicas, int ready, int updated, int partition = 0,
        string currentRevision = "revision-1", string updateRevision = "revision-1", string strategy = "RollingUpdate")
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "StatefulSet",
            metadata = new { name = "demo", generation = 1 },
            spec = new { replicas, updateStrategy = new { type = strategy, rollingUpdate = new { partition } } },
            status = new { observedGeneration = 1, readyReplicas = ready, updatedReplicas = updated, currentRevision, updateRevision }
        });

    private static string ReplicaSet(int observedGeneration = 1)
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "ReplicaSet",
            metadata = new { name = "demo", generation = 1 },
            spec = new { selector = new { matchLabels = new Dictionary<string, string> { ["app"] = "demo" } } },
            status = new { observedGeneration, readyReplicas = 3 }
        });

    private static string ReplicationController(int observedGeneration = 1)
        => Serialize(new
        {
            apiVersion = "v1",
            kind = "ReplicationController",
            metadata = new { name = "demo", generation = 1 },
            spec = new { selector = new Dictionary<string, string> { ["app"] = "demo" } },
            status = new { observedGeneration }
        });

    private static string Pod(string phase, bool ready)
        => Serialize(new
        {
            apiVersion = "v1",
            kind = "Pod",
            metadata = new { name = "demo" },
            status = new { phase, conditions = new[] { new { type = "Ready", status = ready ? "True" : "False" } } }
        });

    private static string PodList(bool ready)
        => Serialize(new
        {
            apiVersion = "v1",
            kind = "PodList",
            items = new[]
            {
                new
                {
                    metadata = new { name = "selected-pod" },
                    status = new { phase = "Running", conditions = new[] { new { type = "Ready", status = ready ? "True" : "False" } } }
                }
            }
        });

    private static string Pvc(string phase)
        => Serialize(new { apiVersion = "v1", kind = "PersistentVolumeClaim", metadata = new { name = "demo" }, status = new { phase } });

    private static string Endpoints(bool ready)
        => Serialize(new
        {
            apiVersion = "v1",
            kind = "Endpoints",
            metadata = new { name = "demo" },
            subsets = new[]
            {
                new
                {
                    addresses = ready ? new[] { new { ip = "10.0.0.1" } } : [],
                    notReadyAddresses = new[] { new { ip = "10.0.0.2" } }
                }
            }
        });

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public sealed record ReadinessCase(
        string Kind,
        string ApiVersion,
        string ResourcePath,
        string ResourceJson,
        string? AuxiliaryPath,
        string? AuxiliaryJson,
        ReadinessOutcome Outcome)
    {
        public override string ToString() => $"{Kind}: {Outcome}";
    }

    public enum ReadinessOutcome
    {
        Ready,
        Pending,
        Failed
    }
}
