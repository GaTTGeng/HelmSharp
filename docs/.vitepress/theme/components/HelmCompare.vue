<template>
  <div class="compare-tool">
    <div class="compare-header">
      <h1>{{ t.title }}</h1>
      <p class="compare-subtitle">{{ t.subtitle }}</p>
    </div>

    <!-- Upload Section -->
    <div class="upload-section" v-if="state === 'idle' || state === 'error'">
      <div
        class="drop-zone"
        :class="{ active: fileDropActive, 'has-file': !!chartFile, 'has-error': !!frontendValidation }"
        @dragover.prevent="fileDropActive = true"
        @dragleave.prevent="fileDropActive = false"
        @drop.prevent="onFileDrop"
      >
        <div v-if="!chartFile" class="drop-prompt">
          <div class="drop-icon">📦</div>
          <div class="drop-text">{{ t.dropText }}<a href="javascript:void(0)" @click="triggerFileInput">{{ t.clickText }}</a>Helm Chart</div>
          <div class="drop-hint">{{ t.dropHint }}</div>
        </div>
        <div v-else class="file-info">
          <span class="file-name">📦 {{ chartFile?.name }}</span>
          <span class="file-size">({{ formatSize(chartFile?.size) }})</span>
          <button class="btn-clear" @click="clearFile">✕</button>
        </div>
        <input
          ref="fileInputRef"
          type="file"
          accept=".tgz,.tar.gz"
          class="hidden-input"
          @change="onFileChange"
        />
      </div>
      <div class="validation-error" v-if="frontendValidation">{{ frontendValidation }}</div>

      <div class="values-section">
        <div class="values-header">
          <label class="values-label">{{ t.valuesLabel }}</label>
        </div>
        <textarea
          class="values-editor"
          v-model="valuesContent"
          :placeholder="t.valuesPlaceholder"
          rows="6"
          spellcheck="false"
        ></textarea>
      </div>

      <div class="submit-row">
        <button class="btn-submit" :disabled="!canSubmit" @click="submitCompare">
          {{ t.submitBtn }}
        </button>
      </div>

      <div class="error-banner" v-if="state === 'error' && errorMessage">
        <div class="error-icon">❌</div>
        <div class="error-text">{{ errorMessage }}</div>
        <button class="btn-retry" @click="reset">{{ t.retry }}</button>
      </div>
    </div>

    <!-- Loading -->
    <div class="loading-section" v-if="state === 'uploading' || state === 'rendering'">
      <div class="spinner"></div>
      <div class="loading-text">{{ state === 'uploading' ? t.uploading : t.rendering }}</div>
    </div>

    <!-- Results -->
    <div class="results-section" v-if="state === 'done' && result">
      <div class="results-toolbar">
        <div class="summary-badge" :class="summaryClass">
          <span class="summary-icon">{{ summaryText }}</span>
          <span class="summary-detail">{{ summaryDetail }}</span>
        </div>
        <div class="chart-meta" v-if="result.chartName">
          <span class="chart-label">{{ result.chartName }}</span>
          <span class="chart-version" v-if="result.chartVersion">v{{ result.chartVersion }}</span>
        </div>
        <button class="btn-retry" @click="reset">{{ t.reupload }}</button>
      </div>

      <div class="lint-warnings" v-if="result.lintWarnings && result.lintWarnings.length > 0">
        <div class="lint-header">{{ t.lintHeader }}</div>
        <ul class="lint-list">
          <li v-for="(w, i) in result.lintWarnings" :key="i">{{ w }}</li>
        </ul>
      </div>

      <div class="error-card" v-if="result.helmError">
        <div class="error-card-header">{{ t.helmError }}</div>
        <pre class="error-card-body">{{ result.helmError }}</pre>
      </div>

      <div class="error-card" v-if="result.helmSharpError">
        <div class="error-card-header">{{ t.helmSharpError }}</div>
        <pre class="error-card-body">{{ result.helmSharpError }}</pre>
      </div>

      <!-- Side-by-side Diff -->
      <div class="diff-container" v-if="diffLines.length > 0">
        <div class="diff-header">
          <div class="diff-header-left">{{ t.headerLeft }}</div>
          <div class="diff-header-right">{{ t.headerRight }}</div>
        </div>
        <div class="diff-body">
          <div v-for="(line, idx) in diffLines" :key="idx" class="diff-row" :class="line.type">
            <div class="diff-left">
              <span class="line-num" v-if="line.leftNum">{{ line.leftNum }}</span>
              <span class="line-num empty" v-else></span>
              <span class="line-content" v-html="escapeHtml(line.left) || '&nbsp;'"></span>
            </div>
            <div class="diff-right">
              <span class="line-num" v-if="line.rightNum">{{ line.rightNum }}</span>
              <span class="line-num empty" v-else></span>
              <span class="line-content" v-html="escapeHtml(line.right) || '&nbsp;'"></span>
            </div>
          </div>
        </div>
      </div>

      <div class="all-match" v-if="!result.helmSharpError && diffLines.length > 0 && result.isMatch">
        <div class="all-match-icon">✅</div>
        <div class="all-match-text">{{ t.allMatchText }}</div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { useData } from 'vitepress'

const { lang } = useData()

const isZh = computed(() => lang.value === 'zh-CN')

const t = computed(() => {
  const zh = isZh.value
  return {
    title: 'Helm Compare',
    subtitle: zh
      ? '对比 HelmSharp 与 Helm CLI 的渲染结果，直观感受 HelmSharp 的兼容性水平。'
      : 'Compare HelmSharp rendering output against the Helm CLI side-by-side.',
    dropText: zh ? '拖拽或' : 'Drag & drop or ',
    clickText: zh ? '点击上传' : 'click to upload',
    dropHint: zh ? '仅支持 .tgz 格式，最大 10 MB' : 'Only .tgz files, max 10 MB',
    valuesLabel: zh ? '⚙️ 自定义 Values（可选）' : '⚙️ Custom Values (optional)',
    valuesPlaceholder: 'replicaCount: 3\nimage:\n  tag: latest',
    submitBtn: zh ? '开始对比渲染' : 'Start Compare',
    uploading: zh ? '正在上传并渲染...' : 'Uploading and rendering...',
    rendering: zh ? '渲染中...' : 'Rendering...',
    retry: zh ? '重试' : 'Retry',
    reupload: zh ? '← 重新上传' : '← Upload Another',
    helmError: zh ? '❌ Helm CLI 错误' : '❌ Helm CLI Error',
    helmSharpError: zh ? '❌ HelmSharp 渲染失败' : '❌ HelmSharp Render Failed',
    lintHeader: zh ? '⚠️ Helm Lint 警告:' : '⚠️ Helm Lint Warnings:',
    headerLeft: 'Helm CLI',
    headerRight: 'HelmSharp',
    allMatchText: zh ? '所有模板渲染结果完全一致' : 'All templates rendered identically',
    helmSharpFail: zh ? '❌ HelmSharp 渲染失败' : '❌ HelmSharp Render Failed',
    fullyMatch: zh ? '✅ 完全一致' : '✅ Identical',
    hasDiff: zh ? '⚠️ 存在差异' : '⚠️ Differences Found',
    templatesMatch: (n: number) => zh ? `${n}/${n} 模板匹配` : `${n}/${n} templates match`,
    templateCount: (n: number) => zh ? `${n} 个模板` : `${n} templates`,
    templateDiff: (n: number) => zh ? `${n} 个模板，存在差异` : `${n} templates, differences found`,
    wrongFormat: zh ? '请上传 .tgz 格式的 Helm Chart 包' : 'Please upload a .tgz Helm Chart package',
    fileTooBig: zh ? '文件大小不能超过 10 MB' : 'File size must not exceed 10 MB',
    uploadError: zh ? '请使用 multipart/form-data 上传文件' : 'Please use multipart/form-data to upload',
    noChart: zh ? '请上传 Helm Chart 文件 (.tgz)' : 'Please upload a Helm Chart file (.tgz)',
    notHelmChart: zh ? '这个包里没有找到 Chart.yaml，请确认上传的是 Helm Chart 文件' : 'No Chart.yaml found in the archive — please upload a valid Helm Chart',
    requestFailed: (status: number) => zh ? `请求失败 (${status})` : `Request failed (${status})`,
    connectFailed: (msg: string) => zh ? `连接失败: ${msg}` : `Connection failed: ${msg}`,
  }
})

interface RenderResult {
  chartName?: string
  chartVersion?: string
  helmOutput?: string
  helmSharpOutput?: string
  helmError?: string
  helmSharpError?: string
  lintWarnings?: string[]
  isMatch?: boolean
  templatesCompared?: number
}

interface DiffLine {
  type: 'equal' | 'added' | 'removed'
  left: string
  right: string
  leftNum: number | null
  rightNum: number | null
}

const API_BASE = import.meta.env.VITE_HELM_COMPARE_API || ''

const state = ref('idle')
const errorMessage = ref('')
const chartFile = ref<File | null>(null)
const valuesContent = ref('')
const fileDropActive = ref(false)
const frontendValidation = ref('')
const fileInputRef = ref<HTMLInputElement | null>(null)
const result = ref<RenderResult | null>(null)

const canSubmit = computed(() => chartFile.value !== null && !frontendValidation.value)

const summaryClass = computed(() => {
  if (!result.value) return ''
  if (result.value.helmSharpError) return 'error'
  if (result.value.isMatch) return 'success'
  return 'warning'
})

const summaryText = computed(() => {
  if (!result.value) return ''
  if (result.value.helmSharpError) return t.value.helmSharpFail
  if (result.value.isMatch) return t.value.fullyMatch
  return t.value.hasDiff
})

const summaryDetail = computed(() => {
  if (!result.value) return ''
  const n = result.value.templatesCompared || 0
  if (result.value.helmSharpError) return t.value.templateCount(n)
  if (result.value.isMatch) return t.value.templatesMatch(n)
  return t.value.templateDiff(n)
})

const diffLines = computed<DiffLine[]>(() => {
  const r = result.value
  if (!r || !r.helmOutput || !r.helmSharpOutput || r.helmSharpError) return []

  const leftLines = r.helmOutput.split('\n')
  const rightLines = r.helmSharpOutput.split('\n')
  const lcs = computeLCS(leftLines, rightLines)
  return buildAlignment(leftLines, rightLines, lcs)
})

function computeLCS(a: string[], b: string[]): number[][] {
  const m = a.length
  const n = b.length
  const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0))
  for (let i = 1; i <= m; i++) {
    for (let j = 1; j <= n; j++) {
      if (a[i - 1] === b[j - 1]) {
        dp[i][j] = dp[i - 1][j - 1] + 1
      } else {
        dp[i][j] = Math.max(dp[i - 1][j], dp[i][j - 1])
      }
    }
  }
  return dp
}

function buildAlignment(a: string[], b: string[], dp: number[][]): DiffLine[] {
  const result: DiffLine[] = []
  let i = a.length
  let j = b.length

  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && a[i - 1] === b[j - 1]) {
      result.unshift({ type: 'equal', left: a[i - 1], right: b[j - 1], leftNum: i, rightNum: j })
      i--
      j--
    } else if (j > 0 && (i === 0 || dp[i][j - 1] >= dp[i - 1][j])) {
      result.unshift({ type: 'added', left: '', right: b[j - 1], leftNum: null, rightNum: j })
      j--
    } else {
      result.unshift({ type: 'removed', left: a[i - 1], right: '', leftNum: i, rightNum: null })
      i--
    }
  }

  return result
}

function escapeHtml(text: string): string {
  if (!text) return '&nbsp;'
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

function formatSize(bytes: number | undefined): string {
  if (!bytes) return '0 KB'
  if (bytes < 1024) return bytes + ' B'
  return (bytes / 1024).toFixed(1) + ' KB'
}

function triggerFileInput() {
  fileInputRef.value?.click()
}

function onFileDrop(e: DragEvent) {
  fileDropActive.value = false
  const files = e.dataTransfer?.files
  if (files && files.length > 0) {
    handleFile(files[0])
  }
}

function onFileChange(e: Event) {
  const target = e.target as HTMLInputElement
  const files = target.files
  if (files && files.length > 0) {
    handleFile(files[0])
  }
}

function handleFile(file: File) {
  frontendValidation.value = ''
  const name = file.name.toLowerCase()

  if (!name.endsWith('.tgz') && !name.endsWith('.tar.gz')) {
    frontendValidation.value = '请上传 .tgz 格式的 Helm Chart 包'
    chartFile.value = null
    return
  }

  if (file.size > 10 * 1024 * 1024) {
    frontendValidation.value = '文件大小不能超过 10 MB'
    chartFile.value = null
    return
  }

  chartFile.value = file
}

function clearFile() {
  chartFile.value = null
  frontendValidation.value = ''
  if (fileInputRef.value) {
    fileInputRef.value.value = ''
  }
}

async function submitCompare() {
  if (!chartFile.value) return

  state.value = 'uploading'
  errorMessage.value = ''

  try {
    const formData = new FormData()
    formData.append('chart', chartFile.value)

    if (valuesContent.value.trim()) {
      const blob = new Blob([valuesContent.value], { type: 'application/x-yaml' })
      formData.append('values', blob, 'values.yaml')
    }

    const url = API_BASE ? `${API_BASE}/api/v1/render` : '/api/v1/render'
    const res = await fetch(url, {
      method: 'POST',
      body: formData,
    })

    const data = await res.json()

    if (!res.ok) {
      state.value = 'error'
      errorMessage.value = data.error || `请求失败 (${res.status})`
      return
    }

    result.value = data as RenderResult
    state.value = 'done'
  } catch (e: any) {
    state.value = 'error'
    errorMessage.value = `连接失败: ${e.message}`
  }
}

function reset() {
  state.value = 'idle'
  result.value = null
  errorMessage.value = ''
}
</script>

<style scoped>
.compare-tool {
  max-width: 1200px;
  margin: 0 auto;
  padding: 2rem 1.5rem 4rem;
}

.compare-header {
  text-align: center;
  margin-bottom: 2rem;
}

.compare-header h1 {
  font-size: 2rem;
  font-weight: 700;
  color: var(--vp-c-brand-1);
  margin-bottom: 0.5rem;
}

.compare-subtitle {
  color: var(--vp-c-text-2);
  font-size: 0.95rem;
}

/* Upload */
.drop-zone {
  border: 2px dashed var(--vp-c-divider);
  border-radius: 8px;
  padding: 2.5rem 2rem;
  text-align: center;
  cursor: pointer;
  transition: border-color 0.2s, background 0.2s;
  background: var(--vp-c-bg-soft);
}

.drop-zone:hover,
.drop-zone.active {
  border-color: var(--vp-c-brand-1);
  background: var(--vp-c-brand-soft);
}

.drop-zone.has-file {
  border-style: solid;
  border-color: var(--vp-c-brand-2);
}

.drop-zone.has-error {
  border-color: #dc2626;
}

.drop-icon {
  font-size: 2.5rem;
  margin-bottom: 0.75rem;
}

.drop-text {
  font-size: 1rem;
  color: var(--vp-c-text-1);
}

.drop-text a {
  color: var(--vp-c-brand-1);
  font-weight: 600;
  text-decoration: underline;
}

.drop-hint {
  font-size: 0.8rem;
  color: var(--vp-c-text-3);
  margin-top: 0.5rem;
}

.file-info {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  font-size: 1rem;
}

.file-name {
  font-weight: 600;
  color: var(--vp-c-text-1);
}

.file-size {
  color: var(--vp-c-text-3);
  font-size: 0.85rem;
}

.btn-clear {
  background: none;
  border: none;
  color: var(--vp-c-text-3);
  cursor: pointer;
  font-size: 1.1rem;
  padding: 0 0.3rem;
}

.btn-clear:hover {
  color: #dc2626;
}

.hidden-input {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

.validation-error {
  color: #dc2626;
  font-size: 0.85rem;
  margin-top: 0.5rem;
  text-align: center;
}

/* Values */
.values-section {
  margin-top: 1.5rem;
}

.values-label {
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--vp-c-text-1);
  margin-bottom: 0.5rem;
}

.values-editor {
  width: 100%;
  padding: 0.75rem 1rem;
  border: 1px solid var(--vp-c-divider);
  border-radius: 6px;
  background: var(--vp-c-bg);
  color: var(--vp-c-text-1);
  font-family: 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace;
  font-size: 0.85rem;
  line-height: 1.5;
  resize: vertical;
  box-sizing: border-box;
}

.values-editor:focus {
  outline: none;
  border-color: var(--vp-c-brand-1);
}

/* Submit */
.submit-row {
  margin-top: 1.5rem;
  text-align: center;
}

.btn-submit {
  padding: 0.7rem 2rem;
  background: var(--vp-c-brand-1);
  color: #fff;
  border: none;
  border-radius: 6px;
  font-size: 1rem;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.2s;
}

.btn-submit:hover:not(:disabled) {
  background: var(--vp-c-brand-2);
}

.btn-submit:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Loading */
.loading-section {
  text-align: center;
  padding: 3rem;
}

.spinner {
  width: 36px;
  height: 36px;
  border: 3px solid var(--vp-c-divider);
  border-top-color: var(--vp-c-brand-1);
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
  margin: 0 auto 1rem;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

.loading-text {
  color: var(--vp-c-text-2);
  font-size: 0.95rem;
}

/* Results */
.results-toolbar {
  display: flex;
  align-items: center;
  gap: 1rem;
  margin-bottom: 1.5rem;
  flex-wrap: wrap;
}

.summary-badge {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 1rem;
  border-radius: 6px;
  font-weight: 600;
  font-size: 0.9rem;
}

.summary-badge.success {
  background: rgba(22, 163, 74, 0.12);
  color: #16a34a;
}

.summary-badge.warning {
  background: rgba(234, 179, 8, 0.12);
  color: #ca8a04;
}

.summary-badge.error {
  background: rgba(220, 38, 38, 0.12);
  color: #dc2626;
}

.chart-meta {
  display: flex;
  align-items: center;
  gap: 0.4rem;
}

.chart-label {
  font-weight: 600;
  color: var(--vp-c-text-1);
}

.chart-version {
  color: var(--vp-c-text-3);
  font-size: 0.85rem;
}

.btn-retry {
  margin-left: auto;
  padding: 0.4rem 1rem;
  background: var(--vp-c-bg-soft);
  border: 1px solid var(--vp-c-divider);
  border-radius: 6px;
  color: var(--vp-c-text-1);
  cursor: pointer;
  font-size: 0.85rem;
  transition: background 0.2s;
}

.btn-retry:hover {
  background: var(--vp-c-divider);
}

/* Error / Lint */
.error-banner {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-top: 1.5rem;
  padding: 1rem 1.25rem;
  background: rgba(220, 38, 38, 0.08);
  border: 1px solid rgba(220, 38, 38, 0.3);
  border-radius: 6px;
}

.error-text {
  color: #dc2626;
  font-size: 0.9rem;
  flex: 1;
}

.lint-warnings {
  margin-bottom: 1rem;
  padding: 0.75rem 1rem;
  background: rgba(234, 179, 8, 0.08);
  border: 1px solid rgba(234, 179, 8, 0.3);
  border-radius: 6px;
}

.lint-header {
  font-weight: 600;
  font-size: 0.85rem;
  color: #ca8a04;
  margin-bottom: 0.3rem;
}

.lint-list {
  margin: 0;
  padding-left: 1.2rem;
  font-size: 0.8rem;
  color: var(--vp-c-text-2);
}

.error-card {
  margin-bottom: 1rem;
  border: 1px solid rgba(220, 38, 38, 0.3);
  border-radius: 6px;
  overflow: hidden;
}

.error-card-header {
  background: rgba(220, 38, 38, 0.1);
  padding: 0.5rem 1rem;
  font-weight: 600;
  font-size: 0.85rem;
  color: #dc2626;
}

.error-card-body {
  padding: 0.75rem 1rem;
  margin: 0;
  font-size: 0.8rem;
  background: var(--vp-c-bg);
  overflow-x: auto;
  white-space: pre-wrap;
  word-break: break-all;
}

/* Diff */
.diff-container {
  border: 1px solid var(--vp-c-divider);
  border-radius: 6px;
  overflow: hidden;
}

.diff-header {
  display: flex;
  background: var(--vp-c-bg-soft);
  border-bottom: 1px solid var(--vp-c-divider);
}

.diff-header-left,
.diff-header-right {
  flex: 1;
  padding: 0.5rem 1rem;
  font-weight: 700;
  font-size: 0.85rem;
  text-align: center;
  color: var(--vp-c-text-1);
}

.diff-header-left {
  border-right: 1px solid var(--vp-c-divider);
}

.diff-body {
  font-family: 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace;
  font-size: 0.78rem;
  line-height: 1.55;
}

.diff-row {
  display: flex;
  min-height: 1.55em;
}

.diff-row.equal {
  background: transparent;
}

.diff-row.added {
  background: rgba(22, 163, 74, 0.08);
}

.diff-row.removed {
  background: rgba(220, 38, 38, 0.08);
}

.diff-left,
.diff-right {
  flex: 1;
  display: flex;
  padding: 0 0.25rem;
  min-width: 0;
}

.diff-left {
  border-right: 1px solid var(--vp-c-divider);
}

.line-num {
  display: inline-block;
  width: 2.5rem;
  min-width: 2.5rem;
  text-align: right;
  padding-right: 0.5rem;
  color: var(--vp-c-text-3);
  user-select: none;
  flex-shrink: 0;
}

.line-num.empty {
  visibility: hidden;
}

.line-content {
  white-space: pre;
  overflow-x: auto;
  flex: 1;
}

.diff-row.added .diff-right .line-content {
  color: #16a34a;
}

.diff-row.removed .diff-left .line-content {
  color: #dc2626;
}

/* All match */
.all-match {
  text-align: center;
  padding: 2rem;
  background: rgba(22, 163, 74, 0.06);
  border-radius: 6px;
  margin-top: 1rem;
}

.all-match-icon {
  font-size: 2rem;
  margin-bottom: 0.5rem;
}

.all-match-text {
  color: #16a34a;
  font-weight: 600;
}
</style>
