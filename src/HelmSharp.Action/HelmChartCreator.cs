using System.Text;

namespace HelmSharp.Action;

/// <summary>
/// Scaffolds new Helm charts, matching `helm create` behavior.
/// </summary>
internal static class HelmChartCreator
{
    private static readonly Dictionary<string, string> StarterTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = "default-starter"
    };

    /// <summary>
    /// Creates a new chart directory with default scaffold files.
    /// </summary>
    public static async Task<string> CreateAsync(
        string chartName,
        string? destination = null,
        string? starter = null,
        CancellationToken cancellationToken = default)
    {
        var destDir = destination ?? Directory.GetCurrentDirectory();
        var chartDir = Path.Combine(destDir, chartName);

        if (Directory.Exists(chartDir) && Directory.GetFileSystemEntries(chartDir).Length > 0)
            throw new InvalidOperationException($"Directory '{chartDir}' already exists and is not empty");

        Directory.CreateDirectory(chartDir);

        if (starter is not null)
        {
            await CopyStarterAsync(starter, chartDir, chartName, cancellationToken);
        }
        else
        {
            await CreateDefaultChartAsync(chartDir, chartName, cancellationToken);
        }

        return chartDir;
    }

    private static async Task CreateDefaultChartAsync(string chartDir, string chartName, CancellationToken ct)
    {
        var templatesDir = Path.Combine(chartDir, "templates");
        var chartsDir = Path.Combine(chartDir, "charts");
        Directory.CreateDirectory(templatesDir);
        Directory.CreateDirectory(chartsDir);

        await File.WriteAllTextAsync(Path.Combine(chartDir, "Chart.yaml"), $"""
            apiVersion: v2
            name: {chartName}
            description: A Helm chart for Kubernetes
            type: application
            version: 0.1.0
            appVersion: "1.16.0"
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(chartDir, "values.yaml"), """
            replicaCount: 1

            image:
              repository: nginx
              pullPolicy: IfNotPresent
              tag: ""

            imagePullSecrets: []
            nameOverride: ""
            fullnameOverride: ""

            serviceAccount:
              create: true
              automount: true
              annotations: {}
              name: ""

            podAnnotations: {}
            podLabels: {}

            podSecurityContext: {}
            securityContext: {}

            service:
              type: ClusterIP
              port: 80

            ingress:
              enabled: false
              className: ""
              annotations: {}
              hosts:
              - host: chart-example.local
                paths:
                - path: /
                  pathType: ImplementationSpecific
              tls: []

            resources: {}
            nodeSelector: {}
            tolerations: []
            affinity: ""
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "_helpers.tpl"), """
            {{/*
            Expand the name of the chart.
            */}}
            {{- define "CHART.name" -}}
            {{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
            {{- end }}

            {{/*
            Create a default fully qualified app name.
            */}}
            {{- define "CHART.fullname" -}}
            {{- if .Values.fullnameOverride }}
            {{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
            {{- else }}
            {{- $name := default .Chart.Name .Values.nameOverride }}
            {{- if contains $name .Release.Name }}
            {{- .Release.Name | trunc 63 | trimSuffix "-" }}
            {{- else }}
            {{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
            {{- end }}
            {{- end }}
            {{- end }}

            {{/*
            Create chart name and version as used by the chart label.
            */}}
            {{- define "CHART.chart" -}}
            {{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
            {{- end }}

            {{/*
            Common labels
            */}}
            {{- define "CHART.labels" -}}
            helm.sh/chart: {{ include "CHART.chart" . }}
            {{ include "CHART.selectorLabels" . }}
            {{- if .Chart.AppVersion }}
            app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
            {{- end }}
            app.kubernetes.io/managed-by: {{ .Release.Service }}
            {{- end }}

            {{/*
            Selector labels
            */}}
            {{- define "CHART.selectorLabels" -}}
            app.kubernetes.io/name: {{ include "CHART.name" . }}
            app.kubernetes.io/instance: {{ .Release.Name }}
            {{- end }}

            {{/*
            Create the name of the service account to use
            */}}
            {{- define "CHART.serviceAccountName" -}}
            {{- if .Values.serviceAccount.create }}
            {{- default (include "CHART.fullname" .) .Values.serviceAccount.name }}
            {{- else }}
            {{- default "default" .Values.serviceAccount.name }}
            {{- end }}
            {{- end }}
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "deployment.yaml"), """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{ include "CHART.fullname" . }}
              labels:
                {{- include "CHART.labels" . | nindent 4 }}
            spec:
              replicas: {{ .Values.replicaCount }}
              selector:
                matchLabels:
                  {{- include "CHART.selectorLabels" . | nindent 6 }}
              template:
                metadata:
                  {{- with .Values.podAnnotations }}
                  annotations:
                    {{- toYaml . | nindent 8 }}
                  {{- end }}
                  labels:
                    {{- include "CHART.labels" . | nindent 8 }}
                    {{- with .Values.podLabels }}
                    {{- toYaml . | nindent 8 }}
                    {{- end }}
                spec:
                  {{- with .Values.imagePullSecrets }}
                  imagePullSecrets:
                    {{- toYaml . | nindent 8 }}
                  {{- end }}
                  serviceAccountName: {{ include "CHART.serviceAccountName" . }}
                  containers:
                  - name: {{ .Chart.Name }}
                    image: "{{ .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}"
                    imagePullPolicy: {{ .Values.image.pullPolicy }}
                    ports:
                    - name: http
                      containerPort: {{ .Values.service.port }}
                      protocol: TCP
                    livenessProbe:
                      httpGet:
                        path: /
                        port: http
                    readinessProbe:
                      httpGet:
                        path: /
                        port: http
                    resources:
                      {{- toYaml .Values.resources | nindent 12 }}
                    {{- with .Values.nodeSelector }}
                    nodeSelector:
                      {{- toYaml . | nindent 8 }}
                    {{- end }}
                    {{- with .Values.affinity }}
                    affinity:
                      {{- toYaml . | nindent 8 }}
                    {{- end }}
                    {{- with .Values.tolerations }}
                    tolerations:
                      {{- toYaml . | nindent 8 }}
                    {{- end }}
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "service.yaml"), """
            apiVersion: v1
            kind: Service
            metadata:
              name: {{ include "CHART.fullname" . }}
              labels:
                {{- include "CHART.labels" . | nindent 4 }}
            spec:
              type: {{ .Values.service.type }}
              ports:
              - port: {{ .Values.service.port }}
                targetPort: http
                protocol: TCP
                name: http
              selector:
                {{- include "CHART.selectorLabels" . | nindent 4 }}
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "serviceaccount.yaml"), """
            {{- if .Values.serviceAccount.create -}}
            apiVersion: v1
            kind: ServiceAccount
            metadata:
              name: {{ include "CHART.serviceAccountName" . }}
              labels:
                {{- include "CHART.labels" . | nindent 4 }}
              {{- with .Values.serviceAccount.annotations }}
              annotations:
                {{- toYaml . | nindent 4 }}
              {{- end }}
            {{- end }}
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "ingress.yaml"), """
            {{- if .Values.ingress.enabled -}}
            apiVersion: networking.k8s.io/v1
            kind: Ingress
            metadata:
              name: {{ include "CHART.fullname" . }}
              labels:
                {{- include "CHART.labels" . | nindent 4 }}
              {{- with .Values.ingress.annotations }}
              annotations:
                {{- toYaml . | nindent 4 }}
              {{- end }}
            spec:
              ingressClassName: {{ .Values.ingress.className }}
              {{- if .Values.ingress.tls }}
              tls:
                {{- range .Values.ingress.tls }}
                - hosts:
                    {{- range .hosts }}
                    - {{ . | quote }}
                    {{- end }}
                  secretName: {{ .secretName }}
                {{- end }}
              {{- end }}
              rules:
                {{- range .Values.ingress.hosts }}
                - host: {{ .host | quote }}
                  http:
                    paths:
                      {{- range .paths }}
                      - path: {{ .path }}
                        pathType: {{ .pathType }}
                        backend:
                          service:
                            name: {{ include "CHART.fullname" $ }}
                            port:
                              number: {{ $.Values.service.port }}
                      {{- end }}
                {{- end }}
            {{- end }}
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "hpa.yaml"), """
            {{- if .Values.autoscaling.enabled }}
            apiVersion: autoscaling/v2
            kind: HorizontalPodAutoscaler
            metadata:
              name: {{ include "CHART.fullname" . }}
              labels:
                {{- include "CHART.labels" . | nindent 4 }}
            spec:
              scaleTargetRef:
                apiVersion: apps/v1
                kind: Deployment
                name: {{ include "CHART.fullname" . }}
              minReplicas: {{ .Values.autoscaling.minReplicas }}
              maxReplicas: {{ .Values.autoscaling.maxReplicas }}
              metrics:
                {{- if .Values.autoscaling.targetCPUUtilizationPercentage }}
                - type: Resource
                  resource:
                    name: cpu
                    target:
                      type: Utilization
                      averageUtilization: {{ .Values.autoscaling.targetCPUUtilizationPercentage }}
                {{- end }}
            {{- end }}
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "NOTES.txt"), """
            1. Get the application URL by running these commands:
            {{- if contains "NodePort" .Values.service.type }}
              export NODE_PORT=$(kubectl get --namespace {{ .Release.Namespace }} -o jsonpath="{.spec.ports[0].nodePort}" services {{ include "CHART.fullname" . }})
              export NODE_IP=$(kubectl get nodes --namespace {{ .Release.Namespace }} -o jsonpath="{.items[0].status.addresses[0].address}")
              echo http://$NODE_IP:$NODE_PORT
            {{- else if contains "LoadBalancer" .Values.service.type }}
              NOTE: It may take a few minutes for the LoadBalancer IP to be available.
                kubectl get --namespace {{ .Release.Namespace }} svc {{ include "CHART.fullname" . }} -w
            {{- else if contains "ClusterIP" .Values.service.type }}
              kubectl --namespace {{ .Release.Namespace }} port-forward svc/{{ include "CHART.fullname" . }} 8080:{{ .Values.service.port }}
              echo http://127.0.0.1:8080
            {{- end }}
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(templatesDir, "tests"), string.Empty, ct);
        File.Delete(Path.Combine(templatesDir, "tests"));

        await File.WriteAllTextAsync(Path.Combine(chartDir, ".helmignore"), """
            # Patterns to ignore when building packages.
            .DS_Store
            .git/
            .gitignore
            .bzr/
            .bzrignore
            .hg/
            .hgignore
            .svn/
            *.swp
            *.bak
            *.tmp
            *.orig
            *~
            .project
            .idea/
            *.tmproj
            .vscode/
            """, ct);
    }

    private static async Task CopyStarterAsync(string starter, string chartDir, string chartName, CancellationToken ct)
    {
        if (!Directory.Exists(starter))
            throw new DirectoryNotFoundException($"Starter chart directory not found: {starter}");

        foreach (var srcFile in Directory.EnumerateFiles(starter, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(starter, srcFile);
            var destPath = Path.Combine(chartDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null) Directory.CreateDirectory(destDir);

            var content = await File.ReadAllTextAsync(srcFile, Encoding.UTF8, ct);
            content = chartName != Path.GetFileName(starter)
                ? content.Replace(Path.GetFileName(starter), chartName, StringComparison.Ordinal)
                : content;
            await File.WriteAllTextAsync(destPath, content, Encoding.UTF8, ct);
        }
    }
}
