using HelmSharp.Chart;

namespace HelmSharp.Kube;

/// <summary>Converts known Kubernetes manifest schemas between served API versions.</summary>
internal static class KubernetesManifestSchemaConverter
{
    /// <summary>Converts a manifest to a compatible target API version when its schema is known.</summary>
    internal static bool TryConvertToApiVersion(
        string document,
        ManifestIdentity identity,
        string targetApiVersion,
        out string convertedDocument)
    {
        if (string.Equals(identity.ApiVersion, targetApiVersion, StringComparison.Ordinal))
        {
            convertedDocument = document;
            return true;
        }

        var ingressMigration = identity is
                                   { Kind: "Ingress", ApiVersion: "extensions/v1beta1" or "networking.k8s.io/v1beta1" } &&
                               targetApiVersion == "networking.k8s.io/v1";
        var legacyWorkloadMigration = identity.Kind is "Deployment" or "DaemonSet" or "ReplicaSet" &&
                                     identity.ApiVersion is "extensions/v1beta1" or "apps/v1beta1" or "apps/v1beta2" &&
                                     targetApiVersion == "apps/v1";
        var networkPolicyMigration = identity is { Kind: "NetworkPolicy", ApiVersion: "extensions/v1beta1" } &&
                                     targetApiVersion == "networking.k8s.io/v1";
        var podSecurityPolicyMigration = identity is { Kind: "PodSecurityPolicy", ApiVersion: "extensions/v1beta1" } &&
                                        targetApiVersion == "policy/v1beta1";
        var legacyIngressBetaMigration = identity is { Kind: "Ingress", ApiVersion: "extensions/v1beta1" } &&
                                         targetApiVersion == "networking.k8s.io/v1beta1";
        var horizontalPodAutoscalerMigration = identity.Kind == "HorizontalPodAutoscaler" &&
                                               (identity.ApiVersion, targetApiVersion) is
                                                   ("autoscaling/v1", "autoscaling/v2") or ("autoscaling/v2", "autoscaling/v1");
        if (!ingressMigration && !legacyWorkloadMigration && !networkPolicyMigration &&
            !podSecurityPolicyMigration && !legacyIngressBetaMigration && !horizontalPodAutoscalerMigration)
        {
            convertedDocument = document;
            return false;
        }

        var converted = HelmYaml.DeserializeDictionary(document, StringComparer.Ordinal);
        if (ingressMigration)
            ConvertLegacyIngressSpec(converted);
        else if (legacyWorkloadMigration)
            ConvertLegacyWorkloadSelector(converted);
        else if (horizontalPodAutoscalerMigration &&
                 !TryConvertHorizontalPodAutoscalerSpec(converted, identity.ApiVersion, targetApiVersion))
        {
            convertedDocument = document;
            return false;
        }

        converted["apiVersion"] = targetApiVersion;
        convertedDocument = HelmYaml.Serialize(converted);
        return true;
    }

    private static bool TryConvertHorizontalPodAutoscalerSpec(
        Dictionary<string, object?> document,
        string sourceApiVersion,
        string targetApiVersion)
    {
        if (!document.TryGetValue("spec", out var specValue) || specValue is not Dictionary<string, object?> spec)
            return true;

        if (sourceApiVersion == "autoscaling/v1" && targetApiVersion == "autoscaling/v2")
        {
            if (!spec.Remove("targetCPUUtilizationPercentage", out var targetCpuUtilization))
                return !spec.ContainsKey("metrics");

            if (spec.ContainsKey("metrics"))
                return false;

            spec["metrics"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "Resource",
                    ["resource"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = "cpu",
                        ["target"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "Utilization",
                            ["averageUtilization"] = targetCpuUtilization
                        }
                    }
                }
            };
            return true;
        }

        // autoscaling/v1 cannot represent behavior or non-CPU metrics. Refuse to retag
        // those baselines because their fields would become invisible to three-way merge.
        if (spec.ContainsKey("behavior"))
            return false;

        if (!spec.Remove("metrics", out var metricsValue))
            return true;

        if (metricsValue is not List<object?> metrics || metrics.Count == 0)
            return metricsValue is List<object?>;

        if (metrics.Count != 1 ||
            metrics[0] is not Dictionary<string, object?> metric ||
            !string.Equals(HelmYaml.GetString(metric, "type"), "Resource", StringComparison.Ordinal) ||
            !metric.TryGetValue("resource", out var resourceValue) || resourceValue is not Dictionary<string, object?> resource ||
            !string.Equals(HelmYaml.GetString(resource, "name"), "cpu", StringComparison.Ordinal) ||
            !resource.TryGetValue("target", out var targetValue) || targetValue is not Dictionary<string, object?> target ||
            !string.Equals(HelmYaml.GetString(target, "type"), "Utilization", StringComparison.Ordinal) ||
            !target.TryGetValue("averageUtilization", out var averageUtilization))
        {
            return false;
        }

        spec["targetCPUUtilizationPercentage"] = averageUtilization;
        return true;
    }

    private static void ConvertLegacyWorkloadSelector(Dictionary<string, object?> document)
    {
        if (!document.TryGetValue("spec", out var specValue) || specValue is not Dictionary<string, object?> spec ||
            spec.ContainsKey("selector") ||
            !spec.TryGetValue("template", out var templateValue) || templateValue is not Dictionary<string, object?> template ||
            !template.TryGetValue("metadata", out var metadataValue) || metadataValue is not Dictionary<string, object?> metadata ||
            !metadata.TryGetValue("labels", out var labelsValue) || labelsValue is not Dictionary<string, object?> labels)
        {
            return;
        }

        // extensions/v1beta1 defaulted an omitted workload selector from template labels;
        // apps/v1 requires that selector to be explicit on create and replace.
        spec["selector"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["matchLabels"] = labels
        };
    }

    private static void ConvertLegacyIngressSpec(Dictionary<string, object?> document)
    {
        if (!document.TryGetValue("spec", out var specValue) || specValue is not Dictionary<string, object?> spec)
            return;

        if (spec.Remove("backend", out var legacyDefaultBackend) &&
            legacyDefaultBackend is Dictionary<string, object?> defaultBackend)
        {
            spec["defaultBackend"] = ConvertLegacyIngressBackend(defaultBackend);
        }

        if (!spec.TryGetValue("rules", out var rulesValue) || rulesValue is not List<object?> rules)
            return;

        foreach (var rule in rules.OfType<Dictionary<string, object?>>())
        {
            if (!rule.TryGetValue("http", out var httpValue) || httpValue is not Dictionary<string, object?> http ||
                !http.TryGetValue("paths", out var pathsValue) || pathsValue is not List<object?> paths)
            {
                continue;
            }

            foreach (var path in paths.OfType<Dictionary<string, object?>>())
            {
                if (path.TryGetValue("backend", out var backendValue) && backendValue is Dictionary<string, object?> backend)
                    path["backend"] = ConvertLegacyIngressBackend(backend);

                path.TryAdd("pathType", "ImplementationSpecific");
            }
        }
    }

    private static Dictionary<string, object?> ConvertLegacyIngressBackend(Dictionary<string, object?> backend)
    {
        if (!backend.Remove("serviceName", out var serviceName) ||
            !backend.Remove("servicePort", out var servicePort))
        {
            return backend;
        }

        var port = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (servicePort is string portName)
            port["name"] = portName;
        else
            port["number"] = servicePort;

        backend["service"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = serviceName,
            ["port"] = port
        };
        return backend;
    }
}
