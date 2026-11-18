{{- define "named.line" -}}
name={{ .name }}; label={{ .label }}
{{- end -}}

{{- define "named.block" -}}
block:
  name: {{ .name }}
  label: {{ .label }}
{{- end -}}

{{- define "named.tplLine" -}}
nested={{ .name }}/{{ .label }}
{{- end -}}

{{- define "named.root" -}}
{{ .root.Values.target.name }}:{{ .root.Release.Name }}:{{ .root.Chart.Name }}:{{ .root.Template.Name }}
{{- end -}}

{{- define "named.scoped" -}}
{{ .scope.name }}:{{ .root.Values.target.name }}
{{- end -}}
