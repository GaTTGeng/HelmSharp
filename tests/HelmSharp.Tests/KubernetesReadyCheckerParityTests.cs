using System.Net;
using System.Text.Json;

namespace HelmSharp.Tests;

/// <summary>
/// Table-driven parity tests for readiness predicates against Helm's ReadyChecker
/// semantics (Deployment/DaemonSet/StatefulSet/Service/CRD/Job/Pod outcomes).
/// </summary>
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
                waiter.WaitForReadyAsync(manifest, "ns", waitForJobs: testCase.WaitForJobs));
            Assert.Contains("All 1 resources are ready", messages);
        }
        else if (testCase.Outcome == ReadinessOutcome.Pending)
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                AsyncEnumerableTestExtensions.DrainAsync(
                    waiter.WaitForReadyAsync(manifest, "ns", waitForJobs: testCase.WaitForJobs)));
            Assert.Contains(testCase.Kind, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AsyncEnumerableTestExtensions.DrainAsync(
                    waiter.WaitForReadyAsync(manifest, "ns", waitForJobs: testCase.WaitForJobs)));
            Assert.Contains("demo", exception.Message);
        }
    }

    public static IEnumerable<object[]> ReadinessCases()
    {
        // Helm v3.17.3 ReadyChecker reference: https://github.com/helm/helm/blob/v3.17.3/pkg/kube/ready.go
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("1", 4), ("2", 3)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(paused: true),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 0)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(observedGeneration: 0),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 4)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("1", 4), ("2", 2)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(exceededProgressDeadline: true),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 2)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(maxUnavailable: "50%"),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 2)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(replicas: 2, maxSurge: "0%", maxUnavailable: "1%"),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 1)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(strategyType: "Recreate"),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 3)), ReadinessOutcome.Pending)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(strategyType: "Recreate"),
            "/apis/apps/v1/namespaces/ns/replicasets", ReplicaSetList(("2", 4)), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", DuplicateTemplateReplicaSetList(), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", EqualTimestampReplicaSetList(), ReadinessOutcome.Ready)];
        yield return [CreateCase("Deployment", "apps/v1", "/apis/apps/v1/namespaces/ns/deployments/demo", Deployment(),
            "/apis/apps/v1/namespaces/ns/replicasets", NonControllerReplicaSetList(), ReadinessOutcome.Pending)];
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

        // Helm v3.17.3 serviceReady: ExternalName is ready immediately; ClusterIP must
        // be assigned; LoadBalancer waits for ExternalIPs or LoadBalancer ingress.
        yield return [CreateCase("Service", "v1", "/api/v1/namespaces/ns/services/demo",
            Service(type: "ExternalName", clusterIP: ""), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Service", "v1", "/api/v1/namespaces/ns/services/demo",
            Service(type: "ClusterIP", clusterIP: ""), outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("Service", "v1", "/api/v1/namespaces/ns/services/demo",
            Service(type: "ClusterIP", clusterIP: "10.0.0.1"), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Service", "v1", "/api/v1/namespaces/ns/services/demo",
            Service(type: "LoadBalancer", clusterIP: "10.0.0.1"), outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("Service", "v1", "/api/v1/namespaces/ns/services/demo",
            Service(type: "LoadBalancer", clusterIP: "10.0.0.1", externalIPs: ["203.0.113.10"]),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Service", "v1", "/api/v1/namespaces/ns/services/demo",
            Service(type: "LoadBalancer", clusterIP: "10.0.0.1", loadBalancerIngress: true),
            outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Service", "v1", "/api/v1/namespaces/ns/services/demo",
            Service(type: "LoadBalancer", clusterIP: ""), outcome: ReadinessOutcome.Pending)];

        // Helm v3.17.3 crdReady: Established=True is ready; NamesAccepted=False is
        // deliberately ready (naming conflict does not block install); else pending.
        yield return [CreateCase("CustomResourceDefinition", "apiextensions.k8s.io/v1",
            "/apis/apiextensions.k8s.io/v1/customresourcedefinitions/demo",
            Crd(conditions: [("Established", "True")]), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("CustomResourceDefinition", "apiextensions.k8s.io/v1",
            "/apis/apiextensions.k8s.io/v1/customresourcedefinitions/demo",
            Crd(conditions: [("NamesAccepted", "False")]), outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("CustomResourceDefinition", "apiextensions.k8s.io/v1",
            "/apis/apiextensions.k8s.io/v1/customresourcedefinitions/demo",
            Crd(conditions: [("NamesAccepted", "True")]), outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("CustomResourceDefinition", "apiextensions.k8s.io/v1",
            "/apis/apiextensions.k8s.io/v1/customresourcedefinitions/demo",
            Crd(conditions: []), outcome: ReadinessOutcome.Pending)];

        // Helm v3.17.3 jobReady: fail only when failed > backoffLimit; otherwise wait
        // for Succeeded >= Completions (skipped when completions is unset).
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: 1, succeeded: 1), waitForJobs: true, outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: 1, succeeded: 0), waitForJobs: true, outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: null, succeeded: 0), waitForJobs: true, outcome: ReadinessOutcome.Ready)];
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: 2, succeeded: 1), waitForJobs: true, outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: 1, succeeded: 0, failed: 6, backoffLimit: 6), waitForJobs: true,
            outcome: ReadinessOutcome.Pending)];
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: 1, succeeded: 0, failed: 7, backoffLimit: 6), waitForJobs: true,
            outcome: ReadinessOutcome.Failed)];
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: 1, succeeded: 0, failed: 1, backoffLimit: 0), waitForJobs: true,
            outcome: ReadinessOutcome.Failed)];
        yield return [CreateCase("Job", "batch/v1", "/apis/batch/v1/namespaces/ns/jobs/demo",
            Job(completions: 1, succeeded: 1, conditions: [("Failed", "True", "BackoffLimitExceeded")]),
            waitForJobs: true, outcome: ReadinessOutcome.Failed)];
    }

    private static ReadinessCase CreateCase(
        string kind,
        string apiVersion,
        string resourcePath,
        string resourceJson,
        string? auxiliaryPath = null,
        string? auxiliaryJson = null,
        ReadinessOutcome outcome = ReadinessOutcome.Ready,
        bool waitForJobs = false)
        => new(kind, apiVersion, resourcePath, resourceJson, auxiliaryPath, auxiliaryJson, outcome, waitForJobs);

    private static string Deployment(int observedGeneration = 1, bool paused = false, string maxUnavailable = "25%",
        bool exceededProgressDeadline = false, string strategyType = "RollingUpdate", string maxSurge = "25%", int replicas = 4)
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "Deployment",
            metadata = new { name = "demo", uid = "deployment-uid", generation = 1 },
            spec = new
            {
                replicas,
                paused,
                selector = new { matchLabels = new Dictionary<string, string> { ["app"] = "demo" } },
                strategy = new
                {
                    type = strategyType,
                    rollingUpdate = strategyType == "RollingUpdate" ? new { maxSurge, maxUnavailable } : null
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
                    ownerReferences = new[] { new { apiVersion = "apps/v1", kind = "Deployment", uid = "deployment-uid", controller = true } },
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

    private static string EqualTimestampReplicaSetList()
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "ReplicaSetList",
            items = new[]
            {
                ReplicaSetResource("9", 0, "desired", "2023-01-01T00:00:00Z", "rs-z"),
                ReplicaSetResource("2", 3, "desired", "2023-01-01T00:00:00Z", "rs-a")
            }
        });

    private static string NonControllerReplicaSetList()
        => Serialize(new
        {
            apiVersion = "apps/v1",
            kind = "ReplicaSetList",
            items = new[]
            {
                new
                {
                    metadata = new
                    {
                        name = "rs-non-controller-owner",
                        uid = "rs-non-controller-owner",
                        generation = 1,
                        creationTimestamp = "2020-01-01T00:00:00Z",
                        ownerReferences = new[] { new { apiVersion = "apps/v1", kind = "Deployment", uid = "deployment-uid", controller = false } }
                    },
                    spec = new { template = PodTemplate("desired", includeHash: true, hash: "non-controller") },
                    status = new { observedGeneration = 1, readyReplicas = 4 }
                }
            }
        });

    private static object ReplicaSetResource(string revision, int ready, string image, string createdAt, string? name = null)
        => new
        {
            metadata = new
            {
                name = name ?? $"rs-{revision}",
                uid = $"rs-{revision}",
                generation = 1,
                creationTimestamp = createdAt,
                ownerReferences = new[] { new { apiVersion = "apps/v1", kind = "Deployment", uid = "deployment-uid", controller = true } },
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

    private static string Service(
        string type,
        string clusterIP,
        string[]? externalIPs = null,
        bool loadBalancerIngress = false)
    {
        // Pending LoadBalancer cases omit ingress (Helm treats missing ingress as
        // not ready); ready cases publish at least one LoadBalancer ingress address.
        object loadBalancer = loadBalancerIngress
            ? new { ingress = new[] { new { ip = "203.0.113.10" } } }
            : new Dictionary<string, object>();

        return Serialize(new
        {
            apiVersion = "v1",
            kind = "Service",
            metadata = new { name = "demo" },
            spec = new
            {
                type,
                clusterIP,
                externalIPs
            },
            status = new { loadBalancer }
        });
    }

    private static string Crd((string Type, string Status)[] conditions)
        => Serialize(new
        {
            apiVersion = "apiextensions.k8s.io/v1",
            kind = "CustomResourceDefinition",
            metadata = new { name = "demo" },
            status = new
            {
                conditions = conditions.Select(c => new { type = c.Type, status = c.Status }).ToArray()
            }
        });

    private static string Job(
        int? completions,
        int succeeded,
        int failed = 0,
        int backoffLimit = 6,
        (string Type, string Status, string Reason)[]? conditions = null)
        => Serialize(new
        {
            apiVersion = "batch/v1",
            kind = "Job",
            metadata = new { name = "demo" },
            spec = new { completions, backoffLimit },
            status = new
            {
                succeeded,
                failed,
                conditions = conditions?
                    .Select(c => new { type = c.Type, status = c.Status, reason = c.Reason })
                    .ToArray()
            }
        });

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

    /// <summary>
    /// One readiness scenario: the primary resource JSON, an optional auxiliary object
    /// (e.g. a Job's Pods), the expected readiness outcome, and whether Job waiting is enabled.
    /// </summary>
    public sealed record ReadinessCase(
        string Kind,
        string ApiVersion,
        string ResourcePath,
        string ResourceJson,
        string? AuxiliaryPath,
        string? AuxiliaryJson,
        ReadinessOutcome Outcome,
        bool WaitForJobs = false)
    {
        public override string ToString() => $"{Kind}: {Outcome}";
    }

    /// <summary>Expected result of a readiness poll for a <see cref="ReadinessCase"/>.</summary>
    public enum ReadinessOutcome
    {
        Ready,
        Pending,
        Failed
    }
}
