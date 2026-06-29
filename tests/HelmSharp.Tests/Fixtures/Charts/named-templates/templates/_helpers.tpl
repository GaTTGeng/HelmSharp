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
