using HelmSharp.Chart;

namespace HelmSharp.Kube;

/// <summary>Converts known Kubernetes manifest schemas between served API versions.</summary>
internal static class KubernetesManifestSchemaConverter
{
    /// <summary>Returns the current API version for a recognized legacy built-in schema.</summary>
    internal static string? GetCanonicalApiVersion(ManifestIdentity identity)
        => identity.Kind switch
        {
            "Deployment" or "DaemonSet" or "ReplicaSet" or "StatefulSet"
                when identity.ApiVersion is "extensions/v1beta1" or "apps/v1beta1" or "apps/v1beta2" => "apps/v1",
            "Ingress" when identity.ApiVersion is "extensions/v1beta1" or "networking.k8s.io/v1beta1" => "networking.k8s.io/v1",
            "NetworkPolicy" when identity.ApiVersion == "extensions/v1beta1" => "networking.k8s.io/v1",
            "HorizontalPodAutoscaler" when identity.ApiVersion is "autoscaling/v2beta1" or "autoscaling/v2beta2" => "autoscaling/v2",
            "CronJob" when identity.ApiVersion == "batch/v1beta1" => "batch/v1",
            "PodDisruptionBudget" when identity.ApiVersion == "policy/v1beta1" => "policy/v1",
            "Role" or "RoleBinding" or "ClusterRole" or "ClusterRoleBinding"
                when identity.ApiVersion == "rbac.authorization.k8s.io/v1beta1" => "rbac.authorization.k8s.io/v1",
            "StorageClass" or "CSIDriver" or "CSINode" or "VolumeAttachment" or "CSIStorageCapacity"
                when identity.ApiVersion == "storage.k8s.io/v1beta1" => "storage.k8s.io/v1",
            "PriorityClass" when identity.ApiVersion == "scheduling.k8s.io/v1beta1" => "scheduling.k8s.io/v1",
            "Lease" when identity.ApiVersion == "coordination.k8s.io/v1beta1" => "coordination.k8s.io/v1",
            "RuntimeClass" when identity.ApiVersion == "node.k8s.io/v1beta1" => "node.k8s.io/v1",
            "APIService" when identity.ApiVersion == "apiregistration.k8s.io/v1beta1" => "apiregistration.k8s.io/v1",
            "IngressClass" when identity.ApiVersion == "networking.k8s.io/v1beta1" => "networking.k8s.io/v1",
            "EndpointSlice" when identity.ApiVersion == "discovery.k8s.io/v1beta1" => "discovery.k8s.io/v1",
            "MutatingWebhookConfiguration" or "ValidatingWebhookConfiguration"
                when identity.ApiVersion == "admissionregistration.k8s.io/v1beta1" => "admissionregistration.k8s.io/v1",
            "FlowSchema" or "PriorityLevelConfiguration"
                when identity.ApiVersion is "flowcontrol.apiserver.k8s.io/v1beta1" or "flowcontrol.apiserver.k8s.io/v1beta2" or "flowcontrol.apiserver.k8s.io/v1beta3" => "flowcontrol.apiserver.k8s.io/v1",
            _ => null
        };

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
        var legacyWorkloadMigration = identity.Kind is "Deployment" or "DaemonSet" or "ReplicaSet" or "StatefulSet" &&
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
                                                   ("autoscaling/v1", "autoscaling/v2") or
                                                   ("autoscaling/v2", "autoscaling/v1") or
                                                   ("autoscaling/v2beta1", "autoscaling/v2") or
                                                   ("autoscaling/v2beta2", "autoscaling/v2");
        var cronJobMigration = identity is { Kind: "CronJob", ApiVersion: "batch/v1beta1" } &&
                               targetApiVersion == "batch/v1";
        var podDisruptionBudgetMigration = identity is { Kind: "PodDisruptionBudget", ApiVersion: "policy/v1beta1" } &&
                                           targetApiVersion == "policy/v1";
        var rbacMigration = identity.ApiVersion == "rbac.authorization.k8s.io/v1beta1" &&
                            targetApiVersion == "rbac.authorization.k8s.io/v1" &&
                            identity.Kind is "Role" or "RoleBinding" or "ClusterRole" or "ClusterRoleBinding";
        var storageMigration = identity.ApiVersion == "storage.k8s.io/v1beta1" &&
                               targetApiVersion == "storage.k8s.io/v1" &&
                               identity.Kind is "StorageClass" or "CSIDriver" or "CSINode" or "VolumeAttachment" or "CSIStorageCapacity";
        var apiVersionOnlyMigration = (identity.ApiVersion, targetApiVersion, identity.Kind) is
            ("scheduling.k8s.io/v1beta1", "scheduling.k8s.io/v1", "PriorityClass") or
            ("coordination.k8s.io/v1beta1", "coordination.k8s.io/v1", "Lease") or
            ("node.k8s.io/v1beta1", "node.k8s.io/v1", "RuntimeClass") or
            ("apiregistration.k8s.io/v1beta1", "apiregistration.k8s.io/v1", "APIService") or
            ("networking.k8s.io/v1beta1", "networking.k8s.io/v1", "IngressClass") or
            ("flowcontrol.apiserver.k8s.io/v1beta3", "flowcontrol.apiserver.k8s.io/v1", "FlowSchema");
        var flowControlMigration = (identity.Kind is "FlowSchema" or "PriorityLevelConfiguration") &&
                                   (identity.ApiVersion is "flowcontrol.apiserver.k8s.io/v1beta1" or "flowcontrol.apiserver.k8s.io/v1beta2" or "flowcontrol.apiserver.k8s.io/v1beta3") &&
                                   targetApiVersion == "flowcontrol.apiserver.k8s.io/v1";
        var endpointSliceMigration = identity is { Kind: "EndpointSlice", ApiVersion: "discovery.k8s.io/v1beta1" } &&
                                     targetApiVersion == "discovery.k8s.io/v1";
        var webhookConfigurationMigration = identity.ApiVersion == "admissionregistration.k8s.io/v1beta1" &&
                                            targetApiVersion == "admissionregistration.k8s.io/v1" &&
                                            identity.Kind is "MutatingWebhookConfiguration" or "ValidatingWebhookConfiguration";
        if (!ingressMigration && !legacyWorkloadMigration && !networkPolicyMigration &&
            !podSecurityPolicyMigration && !legacyIngressBetaMigration && !horizontalPodAutoscalerMigration &&
            !cronJobMigration && !podDisruptionBudgetMigration && !rbacMigration && !storageMigration &&
            !endpointSliceMigration && !webhookConfigurationMigration &&
            !apiVersionOnlyMigration && !flowControlMigration)
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
        else if (podDisruptionBudgetMigration)
        {
            ConvertPodDisruptionBudgetSelector(converted);
        }
        else if (flowControlMigration && !ConvertFlowControlConcurrencyShares(converted))
        {
            convertedDocument = document;
            return false;
        }
        else if (endpointSliceMigration)
        {
            ConvertEndpointSliceTopology(converted);
        }
        else if (webhookConfigurationMigration && !TryConvertWebhookConfiguration(converted))
        {
            convertedDocument = document;
            return false;
        }

        converted["apiVersion"] = targetApiVersion;
        convertedDocument = HelmYaml.Serialize(converted);
        return true;
    }

    private static bool ConvertFlowControlConcurrencyShares(Dictionary<string, object?> document)
    {
        if (!string.Equals(HelmYaml.GetString(document, "kind"), "PriorityLevelConfiguration", StringComparison.Ordinal))
            return true;

        if (!document.TryGetValue("spec", out var specValue) || specValue is not Dictionary<string, object?> spec ||
            !spec.TryGetValue("limited", out var limitedValue) || limitedValue is not Dictionary<string, object?> limited)
        {
            return true;
        }

        if (limited.Remove("assuredConcurrencyShares", out var assuredConcurrencyShares))
        {
            if (!limited.TryAdd("nominalConcurrencyShares", assuredConcurrencyShares))
                return false;

            // The beta APIs defaulted an omitted or zero assured share count to 30.
            if (assuredConcurrencyShares is int or long && Convert.ToInt64(assuredConcurrencyShares) == 0)
                limited["nominalConcurrencyShares"] = 30;
            return true;
        }

        if (HelmYaml.GetString(document, "apiVersion") == "flowcontrol.apiserver.k8s.io/v1beta3" &&
            limited.TryGetValue("nominalConcurrencyShares", out var nominalConcurrencyShares) &&
            nominalConcurrencyShares is int or long && Convert.ToInt64(nominalConcurrencyShares) == 0)
        {
            limited["nominalConcurrencyShares"] = 30;
        }

        return true;
    }

    private static void ConvertEndpointSliceTopology(Dictionary<string, object?> document)
    {
        if (!document.TryGetValue("endpoints", out var endpointsValue) || endpointsValue is not List<object?> endpoints)
            return;

        foreach (var endpoint in endpoints.OfType<Dictionary<string, object?>>())
        {
            if (!endpoint.TryGetValue("topology", out var topologyValue) || topologyValue is not Dictionary<string, object?> topology)
                continue;

            var deprecatedTopology = new Dictionary<string, object?>(topology, StringComparer.Ordinal);
            if (topology.Remove("topology.kubernetes.io/zone", out var zone))
            {
                endpoint["zone"] = zone;
                deprecatedTopology.Remove("topology.kubernetes.io/zone");
            }

            if (topology.Remove("kubernetes.io/hostname", out var hostname))
            {
                endpoint.TryAdd("nodeName", hostname);
                deprecatedTopology.Remove("kubernetes.io/hostname");
            }

            if (deprecatedTopology.Count == 0)
                endpoint.Remove("deprecatedTopology");
            else
                endpoint["deprecatedTopology"] = deprecatedTopology;
            endpoint.Remove("topology");
        }
    }

    private static bool TryConvertWebhookConfiguration(Dictionary<string, object?> document)
    {
        if (!document.TryGetValue("webhooks", out var webhooksValue) || webhooksValue is not List<object?> webhooks)
            return true;

        foreach (var webhook in webhooks.OfType<Dictionary<string, object?>>())
        {
            webhook.TryAdd("failurePolicy", "Ignore");
            webhook.TryAdd("matchPolicy", "Exact");
            webhook.TryAdd("timeoutSeconds", 30);
            webhook.TryAdd("admissionReviewVersions", new List<object?> { "v1beta1" });

            // v1 rejects beta side-effect values that it cannot represent without changing behavior.
            if (!webhook.TryGetValue("sideEffects", out var sideEffects) ||
                sideEffects is not string value || value is not ("None" or "NoneOnDryRun"))
                return false;
        }

        return true;
    }

    private static void ConvertPodDisruptionBudgetSelector(Dictionary<string, object?> document)
    {
        if (!document.TryGetValue("spec", out var specValue) || specValue is not Dictionary<string, object?> spec ||
            !spec.TryGetValue("selector", out var selectorValue) ||
            selectorValue is not Dictionary<string, object?> selector || selector.Count != 0)
        {
            return;
        }

        // An empty beta selector matched no pods, while an empty v1 selector matches all pods.
        // A null selector matches no pods in both versions, so preserve the old behavior.
        spec["selector"] = null;
    }

    private static bool TryConvertHorizontalPodAutoscalerSpec(
        Dictionary<string, object?> document,
        string sourceApiVersion,
        string targetApiVersion)
    {
        if (!document.TryGetValue("spec", out var specValue) || specValue is not Dictionary<string, object?> spec)
            return true;

        // v2beta2 graduated without a schema change, so preserve its metrics and behavior verbatim.
        if (sourceApiVersion == "autoscaling/v2beta2" && targetApiVersion == "autoscaling/v2")
            return true;

        // v2beta1 stored metric names, selectors, and targets in a different shape than v2.
        if (sourceApiVersion == "autoscaling/v2beta1" && targetApiVersion == "autoscaling/v2")
            return TryConvertHorizontalPodAutoscalerV2Beta1Metrics(spec);

        if (sourceApiVersion == "autoscaling/v1" && targetApiVersion == "autoscaling/v2")
        {
            if (!spec.Remove("targetCPUUtilizationPercentage", out var targetCpuUtilization))
                return !spec.ContainsKey("metrics");

            if (targetCpuUtilization is null)
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

    private static bool TryConvertHorizontalPodAutoscalerV2Beta1Metrics(Dictionary<string, object?> spec)
    {
        if (!spec.TryGetValue("metrics", out var metricsValue))
            return true;

        if (metricsValue is not List<object?> metrics)
            return false;

        foreach (var metricValue in metrics)
        {
            if (metricValue is not Dictionary<string, object?> metric)
                return false;

            var type = HelmYaml.GetString(metric, "type");
            if (type is "Resource" or "ContainerResource")
            {
                var resourceKey = type == "ContainerResource" ? "containerResource" : "resource";
                if (!metric.TryGetValue(resourceKey, out var resourceValue) ||
                    resourceValue is not Dictionary<string, object?> resource ||
                    !TryConvertBeta1MetricTarget(
                        resource,
                        "targetAverageUtilization", "Utilization", "averageUtilization",
                        "targetAverageValue", "AverageValue", "averageValue"))
                {
                    return false;
                }

                continue;
            }

            if (type is not ("Pods" or "Object" or "External") ||
                !metric.TryGetValue(type.ToLowerInvariant(), out var sourceValue) ||
                sourceValue is not Dictionary<string, object?> source ||
                !source.Remove("metricName", out var metricName) || metricName is not string)
            {
                return false;
            }

            if (type == "Object")
            {
                // v2beta1 object metrics keep the referenced object and selector beside the metric name.
                if (!source.Remove("target", out var describedObject) ||
                    describedObject is not Dictionary<string, object?> ||
                    !TryConvertBeta1ObjectMetricTarget(source))
                {
                    return false;
                }

                source["describedObject"] = describedObject;
            }
            else if (!TryConvertBeta1MetricTarget(
                         source,
                         "targetAverageValue", "AverageValue", "averageValue",
                         "targetValue", "Value", "value"))
            {
                return false;
            }

            var metricIdentifier = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = metricName
            };
            var selectorName = type is "Pods" or "Object" ? "selector" : "metricSelector";
            if (source.Remove(selectorName, out var selector))
                metricIdentifier["selector"] = selector;
            source["metric"] = metricIdentifier;
        }

        return true;
    }

    private static bool TryConvertBeta1ObjectMetricTarget(Dictionary<string, object?> metric)
    {
        var hasTargetValue = metric.Remove("targetValue", out var targetValue);
        var hasAverageValue = metric.Remove("averageValue", out var averageValue);
        if (!hasTargetValue)
            return false;

        // Kubernetes prefers the optional average target when both object metric values are present.
        metric["target"] = hasAverageValue
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "AverageValue",
                ["averageValue"] = averageValue
            }
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "Value",
                ["value"] = targetValue
            };
        return true;
    }

    private static bool TryConvertBeta1MetricTarget(
        Dictionary<string, object?> metric,
        string firstTargetName,
        string firstTargetType,
        string firstTargetValueName,
        string secondTargetName,
        string secondTargetType,
        string secondTargetValueName)
    {
        var hasFirstTarget = metric.Remove(firstTargetName, out var firstTarget);
        var hasSecondTarget = metric.Remove(secondTargetName, out var secondTarget);
        if (hasFirstTarget == hasSecondTarget)
        {
            return false;
        }

        metric["target"] = hasFirstTarget
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = firstTargetType,
                [firstTargetValueName] = firstTarget
            }
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = secondTargetType,
                [secondTargetValueName] = secondTarget
            };
        return true;
    }

    private static void ConvertLegacyWorkloadSelector(Dictionary<string, object?> document)
    {
        if (!document.TryGetValue("spec", out var specValue) || specValue is not Dictionary<string, object?> spec ||
            (spec.TryGetValue("selector", out var selectorValue) && selectorValue is not null) ||
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
