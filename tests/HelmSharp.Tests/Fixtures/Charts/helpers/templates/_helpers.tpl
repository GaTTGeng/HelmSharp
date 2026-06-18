{{- define "helpers.fullname" -}}
{{ .Release.Name }}-{{ .Chart.Name }}
{{- end -}}

{{- define "helpers.name" -}}
{{ .Chart.Name }}
{{- end -}}

{{- define "helpers.chart" -}}
{{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}
