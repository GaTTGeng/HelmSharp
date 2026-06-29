<template>
  <div class="compare-tool">
    <div class="compare-header">
      <h1>{{ t.title }}</h1>
      <p class="compare-subtitle">{{ t.subtitle }}</p>
      <div class="status-bar">
        <!-- Online -->
        <template v-if="serviceStatus === 'online'">
          <span class="status-dot online"></span>
          <span class="version-badge" v-if="helmSharpVersion">HelmSharp {{ helmSharpVersion }}</span>
          <span class="version-sep" v-if="helmSharpVersion && helmVersion">|</span>
          <span class="version-badge" v-if="helmVersion">Helm {{ helmVersion }}</span>
        </template>
        <!-- Offline -->
        <template v-else-if="serviceStatus === 'offline'">
          <span class="status-dot offline"></span>
          <span class="status-text">{{ t.serviceOffline }}</span>
        </template>
        <!-- Checking -->
        <template v-else>
          <span class="status-dot checking"></span>
          <span class="status-text">{{ t.serviceChecking }}</span>
        </template>
      </div>
    </div>

    <!-- Upload + Examples side-by-side -->
    <div class="compare-layout" v-if="state === 'idle' || state === 'error'">
      <div class="compare-main">
        <div
          class="drop-zone"
          :class="{ active: fileDropActive, 'has-file': !!chartFile, 'has-error': !!frontendValidation }"
          @dragover.prevent="fileDropActive = true"
          @dragleave.prevent="fileDropActive = false"
          @drop.prevent="onFileDrop"
        >
          <div v-if="!chartFile" class="drop-prompt">
            <div class="drop-icon">📦</div>
            <div class="drop-text">{{ t.dropText }}<a href="javascript:void(0)" @click="triggerFileInput">{{ t.clickText }}</a> Helm Chart</div>
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
          <label class="values-label">{{ t.valuesLabel }}</label>
          <textarea
            class="values-editor"
            v-model="valuesContent"
            :placeholder="t.valuesPlaceholder"
            rows="5"
            spellcheck="false"
          ></textarea>
        </div>

        <div class="offline-warning" v-if="serviceStatus === 'offline'">
          <span class="offline-warning-icon">⚠️</span>
          <span class="offline-warning-text">{{ t.serviceUnavailable }}</span>
        </div>

        <div class="submit-row">
          <button class="btn-submit" :disabled="!canSubmit || serviceStatus === 'offline'" @click="submitCompare">
            {{ t.submitBtn }}
          </button>
        </div>

        <div class="error-banner" v-if="state === 'error' && errorMessage">
          <span class="error-icon">❌</span>
          <span class="error-text">{{ errorMessage }}</span>
          <button class="btn-retry" @click="reset">{{ t.retry }}</button>
        </div>
      </div>

      <!-- Quick Examples -->
      <div class="examples-sidebar" v-if="examples.length > 0">
        <div class="examples-header">{{ t.examplesHeader }}</div>
        <div class="examples-list">
          <div
            v-for="ex in examples"
            :key="ex.id"
            class="example-card"
            @click="runExample(ex)"
          >
            <div class="example-card-name">{{ ex.name }}</div>
            <div class="example-card-source">{{ ex.source }}</div>
            <div class="example-card-desc">{{ isZh && ex.descriptionZh ? ex.descriptionZh : ex.description }}</div>
            <code class="example-card-values" v-if="ex.defaultValues">{{ ex.defaultValues }}</code>
          </div>
        </div>
      </div>
    </div>

    <!-- Loading -->
    <div class="loading-section" v-if="state === 'rendering'">
      <div class="loading-card">
        <div class="loading-text">{{ t.rendering }}</div>
        <div class="loading-hint">{{ t.loadingHint }}</div>
        <div class="loading-dots" aria-hidden="true">
          <span :class="{ active: loadingStep === 0 }"></span>
          <span :class="{ active: loadingStep === 1 }"></span>
          <span :class="{ active: loadingStep === 2 }"></span>
        </div>
        <div class="loading-progress" aria-hidden="true">
          <span :style="{ width: `${loadingProgress}%` }"></span>
        </div>
      </div>
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

      <!-- Aligned diff output -->
      <div class="diff-view" v-if="result.helmOutput || result.helmSharpOutput">
        <div class="diff-view-header">
          <div class="diff-column-title">
            <span>Helm CLI</span>
            <span class="split-line-count" v-if="!result.helmError">{{ helmLineCount }} lines</span>
          </div>
          <div class="diff-column-title">
            <span>HelmSharp</span>
            <span class="split-line-count" v-if="!result.helmSharpError">{{ helmSharpLineCount }} lines</span>
          </div>
        </div>

        <div class="diff-error-row" v-if="result.helmError || result.helmSharpError">
          <div class="split-error">{{ result.helmError }}</div>
          <div class="split-error">{{ result.helmSharpError }}</div>
        </div>

        <div class="diff-body" v-else>
          <div
            ref="leftDiffPaneRef"
            class="diff-pane"
            :style="{ '--pane-content-width': leftPaneWidth }"
            @scroll="syncDiffScroll('left')"
          >
            <div class="diff-pane-lines">
              <div
                v-for="(line, i) in alignedDiffRows"
                :key="`left-${i}`"
                class="aligned-diff-line"
                :class="line.type"
              >
                <span class="line-num" :class="{ empty: line.leftNum === null }">{{ line.leftNum ?? '' }}</span>
                <code class="line-content" v-html="line.leftHtml"></code>
              </div>
            </div>
          </div>
          <div
            ref="rightDiffPaneRef"
            class="diff-pane"
            :style="{ '--pane-content-width': rightPaneWidth }"
            @scroll="syncDiffScroll('right')"
          >
            <div class="diff-pane-lines">
              <div
                v-for="(line, i) in alignedDiffRows"
                :key="`right-${i}`"
                class="aligned-diff-line"
                :class="line.type"
              >
                <span class="line-num" :class="{ empty: line.rightNum === null }">{{ line.rightNum ?? '' }}</span>
                <code class="line-content" v-html="line.rightHtml"></code>
              </div>
            </div>
          </div>
        </div>

        <div class="diff-scrollbar-row" v-if="!result.helmError && !result.helmSharpError">
          <div class="diff-scroll-cell">
            <div
              class="diff-scroll-track"
              :class="{ disabled: leftScrollMax === 0 }"
              role="slider"
              tabindex="0"
              aria-label="Scroll Helm CLI output horizontally"
              aria-valuemin="0"
              :aria-valuemax="leftScrollMax"
              :aria-valuenow="Math.round(leftScrollValue)"
              @pointerdown="onDiffTrackPointerDown('left', $event)"
              @keydown="onDiffTrackKeydown('left', $event)"
            >
              <span class="diff-scroll-thumb" :style="scrollThumbStyle('left')"></span>
            </div>
          </div>
          <div class="diff-scroll-cell">
            <div
              class="diff-scroll-track"
              :class="{ disabled: rightScrollMax === 0 }"
              role="slider"
              tabindex="0"
              aria-label="Scroll HelmSharp output horizontally"
              aria-valuemin="0"
              :aria-valuemax="rightScrollMax"
              :aria-valuenow="Math.round(rightScrollValue)"
              @pointerdown="onDiffTrackPointerDown('right', $event)"
              @keydown="onDiffTrackKeydown('right', $event)"
            >
              <span class="diff-scroll-thumb" :style="scrollThumbStyle('right')"></span>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, nextTick, onMounted } from 'vue'
import { useData } from 'vitepress'

const { lang } = useData()

// ── Service status & version info ────────────────────────────────────
const serviceStatus = ref<'checking' | 'online' | 'offline'>('checking')
const helmVersion = ref('')
const helmSharpVersion = ref('')

onMounted(async () => {
  try {
    const base = API_BASE || ''
    const url = base ? `${base}/api/v1/health` : '/api/v1/health'
    const res = await fetch(url, { signal: AbortSignal.timeout(5000) })
    const data = await res.json()
    helmVersion.value = data.helmVersion || ''
    helmSharpVersion.value = data.helmSharpVersion || ''
    serviceStatus.value = 'online'
  } catch {
    serviceStatus.value = 'offline'
  }

  if (serviceStatus.value === 'online') {
    try {
      const base = API_BASE || ''
      const url = base ? `${base}/api/v1/examples` : '/api/v1/examples'
      const res = await fetch(url, { signal: AbortSignal.timeout(5000) })
      if (res.ok) examples.value = await res.json()
    } catch { /* ignore */ }
  }
})

interface ExampleInfo {
  id: string
  name: string
  source: string
  description: string
  descriptionZh: string
  defaultValues: string
}

const examples = ref<ExampleInfo[]>([])

async function runExample(example: ExampleInfo) {
  errorMessage.value = ''
  result.value = null
  frontendValidation.value = ''

  try {
    const base = API_BASE || ''
    const chartUrl = base
      ? `${base}/api/v1/examples/${example.id}/chart`
      : `/api/v1/examples/${example.id}/chart`
    const res = await fetch(chartUrl)
    if (!res.ok) {
      state.value = 'error'
      errorMessage.value = t.value.requestFailed(res.status)
      return
    }
    const blob = await res.blob()
    chartFile.value = new File([blob], `${example.id}.tgz`, { type: 'application/gzip' })
    valuesContent.value = example.defaultValues || ''
    state.value = 'idle'
    examplesOpen.value = false
  } catch (e: any) {
    state.value = 'error'
    errorMessage.value = t.value.connectFailed(e.message)
  }
}

function exampleInitial(name: string): string {
  return name.trim().charAt(0).toUpperCase()
}

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
    loadingHint: zh ? '较大 Chart 可能需要数十秒' : 'Large charts may take up to a minute',
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
    serviceChecking: zh ? '检测服务状态...' : 'Checking service...',
    serviceOffline: zh ? '对比服务不可用' : 'Compare service offline',
    serviceUnavailable: zh
      ? '对比服务暂时无法连接，上传功能已禁用。请稍后重试或联系管理员。'
      : 'The compare service is currently unreachable. Upload has been disabled. Please try again later.',
    examplesHeader: zh ? '快速示例' : 'Quick Examples',
    examplesSubtitle: zh ? '公共 chart 样例' : 'Public chart samples',
    close: zh ? '关闭' : 'Close',
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

interface AlignedDiffRow {
  type: 'equal' | 'changed' | 'added' | 'removed'
  left: string
  right: string
  leftNum: number | null
  rightNum: number | null
  leftHtml: string
  rightHtml: string
}

const API_BASE = import.meta.env.VITE_HELM_COMPARE_API || ''

const state = ref('idle')
const errorMessage = ref('')
const chartFile = ref<File | null>(null)
const valuesContent = ref('')
const fileDropActive = ref(false)
const frontendValidation = ref('')
const examplesOpen = ref(false)
const fileInputRef = ref<HTMLInputElement | null>(null)
const result = ref<RenderResult | null>(null)
const loadingProgress = ref(12)
const loadingStep = ref(0)
const leftDiffPaneRef = ref<HTMLElement | null>(null)
const rightDiffPaneRef = ref<HTMLElement | null>(null)
const leftScrollValue = ref(0)
const rightScrollValue = ref(0)
const leftScrollMax = ref(0)
const rightScrollMax = ref(0)

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

const helmLineCount = computed(() => (result.value?.helmOutput || '').split('\n').length)
const helmSharpLineCount = computed(() => (result.value?.helmSharpOutput || '').split('\n').length)
const leftPaneWidth = computed(() => paneWidthFor(result.value?.helmOutput || ''))
const rightPaneWidth = computed(() => paneWidthFor(result.value?.helmSharpOutput || ''))

const alignedDiffRows = computed<AlignedDiffRow[]>(() => {
  const r = result.value
  if (!r || !r.helmOutput || !r.helmSharpOutput || r.helmSharpError) return []

  const leftLines = r.helmOutput.split('\n')
  const rightLines = r.helmSharpOutput.split('\n')
  return alignDiffLines(leftLines, rightLines)
})

function alignDiffLines(leftLines: string[], rightLines: string[]): AlignedDiffRow[] {
  const m = leftLines.length
  const n = rightLines.length
  const gapPenalty = -1.2
  const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0))
  const move: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0))

  for (let i = 1; i <= m; i++) {
    dp[i][0] = i * gapPenalty
    move[i][0] = 1
  }

  for (let j = 1; j <= n; j++) {
    dp[0][j] = j * gapPenalty
    move[0][j] = 2
  }

  for (let i = 1; i <= m; i++) {
    for (let j = 1; j <= n; j++) {
      const similarity = lineSimilarity(leftLines[i - 1], rightLines[j - 1])
      const pairScore = similarity >= 0.48 ? similarity * 3 : -2.5
      const diagonal = dp[i - 1][j - 1] + pairScore
      const removed = dp[i - 1][j] + gapPenalty
      const added = dp[i][j - 1] + gapPenalty

      if (diagonal >= removed && diagonal >= added) {
        dp[i][j] = diagonal
        move[i][j] = 0
      } else if (removed >= added) {
        dp[i][j] = removed
        move[i][j] = 1
      } else {
        dp[i][j] = added
        move[i][j] = 2
      }
    }
  }

  const rows: AlignedDiffRow[] = []
  let i = m
  let j = n

  while (i > 0 || j > 0) {
    const currentMove = move[i][j]

    if (i > 0 && j > 0 && currentMove === 0) {
      const left = leftLines[i - 1]
      const right = rightLines[j - 1]
      const equal = left === right
      rows.unshift({
        type: equal ? 'equal' : 'changed',
        left,
        right,
        leftNum: i,
        rightNum: j,
        leftHtml: equal ? escapeHtml(left) : inlineDiffHtml(left, right, 'left'),
        rightHtml: equal ? escapeHtml(right) : inlineDiffHtml(left, right, 'right'),
      })
      i--
      j--
    } else if (i > 0 && (j === 0 || currentMove === 1)) {
      const left = leftLines[i - 1]
      rows.unshift({
        type: 'removed',
        left,
        right: '',
        leftNum: i,
        rightNum: null,
        leftHtml: escapeHtml(left),
        rightHtml: '',
      })
      i--
    } else {
      const right = rightLines[j - 1]
      rows.unshift({
        type: 'added',
        left: '',
        right,
        leftNum: null,
        rightNum: j,
        leftHtml: '',
        rightHtml: escapeHtml(right),
      })
      j--
    }
  }

  return rows
}

function paneWidthFor(output: string): string {
  const longest = output
    .split('\n')
    .reduce((max, line) => Math.max(max, line.length + 7), 96)

  return `${longest}ch`
}

function updateDiffScrollMetrics() {
  const left = leftDiffPaneRef.value
  const right = rightDiffPaneRef.value

  leftScrollMax.value = left ? Math.max(0, left.scrollWidth - left.clientWidth) : 0
  rightScrollMax.value = right ? Math.max(0, right.scrollWidth - right.clientWidth) : 0
  leftScrollValue.value = left ? Math.min(left.scrollLeft, leftScrollMax.value) : 0
  rightScrollValue.value = right ? Math.min(right.scrollLeft, rightScrollMax.value) : 0
}

function onDiffRangeInput(side: 'left' | 'right', event: Event) {
  const value = Number((event.target as HTMLInputElement).value)
  const pane = side === 'left' ? leftDiffPaneRef.value : rightDiffPaneRef.value
  if (!pane) return

  pane.scrollLeft = value
  if (side === 'left') {
    leftScrollValue.value = value
  } else {
    rightScrollValue.value = value
  }
}

function syncDiffScroll(side: 'left' | 'right') {
  const pane = side === 'left' ? leftDiffPaneRef.value : rightDiffPaneRef.value
  if (!pane) return

  if (side === 'left') {
    leftScrollValue.value = pane.scrollLeft
  } else {
    rightScrollValue.value = pane.scrollLeft
  }
}

function scrollThumbStyle(side: 'left' | 'right'): Record<string, string> {
  const max = side === 'left' ? leftScrollMax.value : rightScrollMax.value
  const val = side === 'left' ? leftScrollValue.value : rightScrollValue.value
  if (max === 0) return { width: '100%', left: '0' }
  const viewRatio = Math.min(1, 0.3)
  const thumbWidth = Math.max(20, viewRatio * 100)
  const thumbLeft = (val / max) * (100 - thumbWidth)
  return {
    width: thumbWidth + '%',
    left: thumbLeft + '%',
  }
}

function onDiffTrackPointerDown(side: 'left' | 'right', e: PointerEvent) {
  e.preventDefault()
  const pane = side === 'left' ? leftDiffPaneRef.value : rightDiffPaneRef.value
  if (!pane) return
  const max = side === 'left' ? leftScrollMax.value : rightScrollMax.value
  if (max === 0) return
  const track = e.currentTarget as HTMLElement
  ;(track as any).setPointerCapture?.(e.pointerId)

  const updatePos = (clientX: number) => {
    const rect = track.getBoundingClientRect()
    const pct = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width))
    const target = pct * max
    if (side === 'left') {
      leftScrollValue.value = target
      pane.scrollLeft = target
    } else {
      rightScrollValue.value = target
      pane.scrollLeft = target
    }
  }

  updatePos(e.clientX)

  const onMove = (ev: PointerEvent) => { updatePos(ev.clientX) }
  const onUp = () => {
    track.removeEventListener('pointermove', onMove)
    track.removeEventListener('pointerup', onUp)
    track.removeEventListener('pointerleave', onUp)
  }
  track.addEventListener('pointermove', onMove)
  track.addEventListener('pointerup', onUp)
  track.addEventListener('pointerleave', onUp)
}

function onDiffTrackKeydown(side: 'left' | 'right', e: KeyboardEvent) {
  const pane = side === 'left' ? leftDiffPaneRef.value : rightDiffPaneRef.value
  if (!pane) return
  const step = 40
  if (e.key === 'ArrowLeft') {
    pane.scrollLeft = Math.max(0, pane.scrollLeft - step)
    if (side === 'left') leftScrollValue.value = pane.scrollLeft
    else rightScrollValue.value = pane.scrollLeft
  } else if (e.key === 'ArrowRight') {
    pane.scrollLeft = pane.scrollLeft + step
    if (side === 'left') leftScrollValue.value = pane.scrollLeft
    else rightScrollValue.value = pane.scrollLeft
  }
}

function lineSimilarity(left: string, right: string): number {
  if (left === right) return 1

  const a = normalizeLine(left)
  const b = normalizeLine(right)

  if (a === b) return 0.96
  if (!a || !b) return 0

  let prefix = 0
  const minLength = Math.min(a.length, b.length)
  while (prefix < minLength && a[prefix] === b[prefix]) prefix++

  let suffix = 0
  while (
    suffix < minLength - prefix &&
    a[a.length - 1 - suffix] === b[b.length - 1 - suffix]
  ) {
    suffix++
  }

  const tokenScore = tokenSimilarity(a, b)
  const edgeScore = (prefix + suffix) / Math.max(a.length, b.length)
  const leftKey = yamlLikeKey(a)
  const rightKey = yamlLikeKey(b)
  const keyBonus = leftKey && leftKey === rightKey ? 0.18 : 0

  return Math.min(1, tokenScore * 0.55 + edgeScore * 0.45 + keyBonus)
}

function normalizeLine(line: string): string {
  return line.trim().replace(/\s+/g, ' ')
}

function yamlLikeKey(line: string): string {
  const match = line.match(/^[-\s]*([A-Za-z0-9_.-]+)\s*:/)
  return match ? match[1] : ''
}

function tokenSimilarity(left: string, right: string): number {
  const leftTokens = tokenizeLine(left)
  const rightTokens = tokenizeLine(right)
  if (leftTokens.size === 0 || rightTokens.size === 0) return 0

  let common = 0
  for (const token of leftTokens) {
    if (rightTokens.has(token)) common++
  }

  return (2 * common) / (leftTokens.size + rightTokens.size)
}

function tokenizeLine(line: string): Set<string> {
  return new Set(line.split(/[^A-Za-z0-9_.-]+/).filter(Boolean))
}

function inlineDiffHtml(left: string, right: string, side: 'left' | 'right'): string {
  const text = side === 'left' ? left : right
  const other = side === 'left' ? right : left
  let prefix = 0
  const minLength = Math.min(text.length, other.length)

  while (prefix < minLength && text[prefix] === other[prefix]) prefix++

  let suffix = 0
  while (
    suffix < minLength - prefix &&
    text[text.length - 1 - suffix] === other[other.length - 1 - suffix]
  ) {
    suffix++
  }

  const start = text.slice(0, prefix)
  const changed = text.slice(prefix, text.length - suffix)
  const end = suffix > 0 ? text.slice(text.length - suffix) : ''
  const marker = side === 'left' ? 'removed' : 'added'
  const highlighted = changed
    ? `<span class="inline-diff ${marker}">${escapeHtml(changed)}</span>`
    : ''

  return `${escapeHtml(start)}${highlighted}${escapeHtml(end)}`
}

function escapeHtml(text: string): string {
  if (!text) return ''
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
    frontendValidation.value = t.value.wrongFormat
    chartFile.value = null
    return
  }

  if (file.size > 10 * 1024 * 1024) {
    frontendValidation.value = t.value.fileTooBig
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

  state.value = 'rendering'
  errorMessage.value = ''
  const renderStartedAt = Date.now()
  loadingProgress.value = 12
  loadingStep.value = 0
  const loadingTimer = window.setInterval(() => {
    loadingStep.value = (loadingStep.value + 1) % 3
    loadingProgress.value = Math.min(
      94,
      loadingProgress.value + Math.max(1.5, (94 - loadingProgress.value) * 0.08),
    )
  }, 90)

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
      errorMessage.value = data.error || t.value.requestFailed(res.status)
      return
    }

    const remainingLoadingMs = 1200 - (Date.now() - renderStartedAt)
    if (remainingLoadingMs > 0) {
      await new Promise(resolve => setTimeout(resolve, remainingLoadingMs))
    }

    result.value = data as RenderResult
    state.value = 'done'
    await nextTick()
    updateDiffScrollMetrics()
  } catch (e: any) {
    state.value = 'error'
    errorMessage.value = t.value.connectFailed(e.message)
  } finally {
    window.clearInterval(loadingTimer)
  }
}

function reset() {
  state.value = 'idle'
  result.value = null
  errorMessage.value = ''
  leftScrollValue.value = 0
  rightScrollValue.value = 0
  leftScrollMax.value = 0
  rightScrollMax.value = 0
}
</script>

<style scoped>
.compare-tool {
  width: 100%;
  max-width: 1080px;
  margin: 0 auto;
  padding: 2rem 1.5rem 4rem;
  box-sizing: border-box;
}

.compare-tool:has(.results-section) {
  max-width: min(86vw, 1440px);
}

.compare-header {
  text-align: center;
  margin-bottom: 2rem;
  animation: compareFadeDown 0.32s ease-out both;
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

.status-bar {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  margin-top: 0.75rem;
}

.version-badge {
  display: inline-block;
  padding: 0.15rem 0.6rem;
  border-radius: 4px;
  font-size: 0.75rem;
  font-weight: 600;
  font-family: ui-monospace, 'Cascadia Code', 'Fira Code', monospace;
  background: var(--vp-c-brand-soft);
  color: var(--vp-c-brand-1);
}

.version-sep {
  color: var(--vp-c-text-3);
  font-size: 0.8rem;
}

.status-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

.status-dot.online {
  background: #16a34a;
  box-shadow: 0 0 6px rgba(22, 163, 74, 0.5);
}

.status-dot.offline {
  background: #dc2626;
  box-shadow: 0 0 6px rgba(220, 38, 38, 0.5);
}

.status-dot.checking {
  background: #ca8a04;
  animation: pulse 1.2s ease-in-out infinite;
}

@keyframes pulse {
  0%, 100% { opacity: 0.4; }
  50% { opacity: 1; }
}

.status-text {
  font-size: 0.8rem;
  color: var(--vp-c-text-2);
}

.offline-warning {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  margin-top: 1rem;
  padding: 0.75rem 1rem;
  background: rgba(220, 38, 38, 0.06);
  border: 1px solid rgba(220, 38, 38, 0.25);
  border-radius: 6px;
}

.offline-warning-icon {
  font-size: 1rem;
  flex-shrink: 0;
  margin-top: 1px;
}

.offline-warning-text {
  font-size: 0.85rem;
  color: #dc2626;
  line-height: 1.5;
}

/* Upload */
.drop-zone {
  border: 2px dashed var(--vp-c-divider);
  border-radius: 8px;
  padding: 2.5rem 2rem;
  min-height: 176px;
  display: flex;
  align-items: center;
  justify-content: center;
  text-align: center;
  cursor: pointer;
  transition: border-color 0.2s, background 0.2s;
  background: var(--vp-c-bg-soft);
  animation: compareFadeUp 0.36s ease-out both;
  box-sizing: border-box;
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

.drop-prompt {
  width: 100%;
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

.drop-text-separator {
  color: var(--vp-c-text-3);
  margin: 0 0.35rem;
}

.drop-example-link {
  display: inline;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--vp-c-brand-1);
  font: inherit;
  font-weight: 600;
  text-decoration: underline;
  cursor: pointer;
}

.drop-example-link:hover,
.drop-example-link:focus-visible {
  color: var(--vp-c-brand-2);
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
  font-size: 1.15rem;
  width: 100%;
  min-width: 0;
  flex-wrap: wrap;
}

.file-name {
  font-weight: 600;
  color: var(--vp-c-text-1);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-size {
  color: var(--vp-c-text-3);
  font-size: 0.95rem;
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
  animation: compareFadeUp 0.36s ease-out 0.05s both;
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
  animation: compareFadeUp 0.36s ease-out 0.1s both;
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
  display: flex;
  justify-content: center;
  padding: 4rem 1rem;
  animation: compareFadeUp 0.24s ease-out both;
}
.loading-card {
  text-align: center;
  padding: 2rem 3rem;
  background: var(--vp-c-bg-soft);
  border: 1px solid var(--vp-c-divider);
  border-radius: 10px;
  box-shadow: 0 16px 42px rgba(0, 0, 0, 0.14);
  animation: loadingCardEnter 0.28s ease-out both;
}
.loading-text {
  color: var(--vp-c-text-1);
  font-size: 1rem;
  font-weight: 600;
  margin-bottom: 0.4rem;
}
.loading-hint {
  color: var(--vp-c-text-3);
  font-size: 0.8rem;
}

.loading-dots {
  display: flex;
  justify-content: center;
  gap: 0.35rem;
  margin-top: 1rem;
}

.loading-dots span {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  background: var(--vp-c-brand-1);
  opacity: 0.35;
  transform: scale(0.85);
  transition: opacity 0.16s ease, transform 0.16s ease;
}

.loading-dots span.active {
  opacity: 1;
  transform: scale(1.25);
}

.loading-progress {
  position: relative;
  width: 220px;
  height: 4px;
  margin: 1.25rem auto 0;
  overflow: hidden;
  border-radius: 999px;
  background: var(--vp-c-divider);
}

.loading-progress span {
  position: absolute;
  top: 0;
  left: 0;
  height: 100%;
  border-radius: inherit;
  background: var(--vp-c-brand-1);
  transition: width 0.16s ease;
}

/* Results */
.results-section {
  animation: compareFadeUp 0.28s ease-out both;
}

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

/* Aligned diff view */
.diff-view {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--vp-c-divider);
  border-radius: 8px;
  overflow: hidden;
  max-height: calc(100vh - 260px);
  background: var(--vp-c-bg);
}

.diff-view-header,
.diff-error-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}

.diff-view-header {
  position: sticky;
  top: 0;
  z-index: 2;
  flex: 0 0 auto;
  background: var(--vp-c-bg-soft);
  border-bottom: 1px solid var(--vp-c-divider);
}

.diff-column-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.55rem 1rem;
  font-weight: 700;
  font-size: 0.85rem;
  color: var(--vp-c-text-1);
}

.diff-column-title:first-child {
  border-right: 1px solid var(--vp-c-divider);
}

.split-line-count {
  font-weight: 400;
  font-size: 0.75rem;
  color: var(--vp-c-text-3);
}

.split-error {
  padding: 0.75rem 1rem;
  color: #dc2626;
  font-size: 0.8rem;
  background: rgba(220, 38, 38, 0.05);
  white-space: pre-wrap;
  word-break: break-word;
}

.diff-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  flex: 1 1 auto;
  min-height: 0;
  font-family: 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace;
  font-size: 0.75rem;
  line-height: 1.55;
  overflow-y: auto;
}

.diff-pane {
  min-width: 0;
  overflow-x: auto;
}

.diff-pane::-webkit-scrollbar {
  width: 0;
  height: 0;
}

.diff-pane:first-child {
  border-right: 1px solid var(--vp-c-divider);
}

.diff-pane-lines {
  display: grid;
  grid-auto-rows: 1.65em;
  width: var(--pane-content-width);
  min-width: 100%;
}

.aligned-diff-line {
  display: grid;
  grid-template-columns: 3rem max-content;
  align-items: start;
  width: 100%;
  min-width: 100%;
  height: 1.65em;
  border-bottom: 1px solid rgba(128, 128, 128, 0.08);
}

.diff-scrollbar-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  flex: 0 0 auto;
  gap: 0;
  padding: 0.5rem 0.75rem;
  border-top: 1px solid var(--vp-c-divider);
  background: var(--vp-c-bg-soft);
}
.diff-scroll-cell {
  min-width: 0;
  display: flex;
  align-items: center;
}
.diff-scroll-cell:first-child {
  padding-right: 0.75rem;
  border-right: 1px solid var(--vp-c-divider);
}
.diff-scroll-cell:last-child {
  padding-left: 0.75rem;
}
.diff-scroll-track {
  width: 100%;
  height: 8px;
  background: var(--vp-c-divider);
  border-radius: 4px;
  cursor: pointer;
  position: relative;
}
.diff-scroll-track.disabled {
  opacity: 0.4;
  cursor: default;
}
.diff-scroll-thumb {
  display: block;
  height: 8px;
  background: var(--vp-c-brand-1);
  border-radius: 4px;
  min-width: 20px;
  position: absolute;
  left: 0;
  top: 0;
}

.diff-scroll-range {
  width: 100%;
  min-width: 0;
  height: 18px;
  margin: 0;
  appearance: none;
  background: transparent;
  cursor: pointer;
  accent-color: var(--vp-c-brand-1);
}

.diff-scroll-range:first-child {
  padding-right: 0.75rem;
  border-right: 1px solid var(--vp-c-divider);
}

.diff-scroll-range:last-child {
  padding-left: 0.75rem;
}

.diff-scroll-range:disabled {
  opacity: 0.35;
  cursor: default;
}

.diff-scroll-range::-webkit-slider-runnable-track {
  height: 6px;
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.38);
}

.diff-scroll-range::-webkit-slider-thumb {
  width: 30px;
  height: 12px;
  margin-top: -3px;
  appearance: none;
  border-radius: 999px;
  background: var(--vp-c-brand-1);
  box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.12);
}

.diff-scroll-range::-moz-range-track {
  height: 6px;
  border: 0;
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.38);
}

.diff-scroll-range::-moz-range-thumb {
  width: 30px;
  height: 12px;
  border: 0;
  border-radius: 999px;
  background: var(--vp-c-brand-1);
  box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.12);
}

.aligned-diff-line.equal {
  background: transparent;
}

.aligned-diff-line.changed {
  background: rgba(234, 179, 8, 0.06);
}

.aligned-diff-line.added {
  background: rgba(22, 163, 74, 0.08);
}

.aligned-diff-line.removed {
  background: rgba(220, 38, 38, 0.08);
}

.line-num {
  display: inline-block;
  width: 3rem;
  min-width: 3rem;
  text-align: right;
  padding-right: 0.5rem;
  color: var(--vp-c-text-3);
  user-select: none;
  box-sizing: border-box;
}

.line-num.empty {
  visibility: hidden;
}

.line-content {
  display: inline;
  white-space: pre;
  padding-right: 0.75rem !important;
  background: transparent !important;
  border-radius: 0 !important;
  color: var(--vp-c-text-1);
}

.diff-pane:last-child .aligned-diff-line.added .line-content,
.inline-diff.added {
  color: #16a34a;
}

.diff-pane:first-child .aligned-diff-line.removed .line-content,
.inline-diff.removed {
  color: #dc2626;
}

/* Two-column layout: upload left, examples right */
.compare-layout {
  display: flex;
  gap: 2rem;
  align-items: flex-start;
}
.compare-main {
  flex: 1;
  min-width: 0;
}
.examples-sidebar {
  flex: 0 0 280px;
  max-height: 70vh;
  overflow-y: auto;
}
@media (max-width: 768px) {
  .compare-layout { flex-direction: column; }
  .examples-sidebar { flex: none; max-height: none; }
}
.examples-header { font-size: 0.85rem; font-weight: 600; color: var(--vp-c-text-1); margin-bottom: 0.6rem; }
.examples-list { display: flex; flex-direction: column; gap: 0.5rem; }
.example-card { border: 1px solid var(--vp-c-divider); border-radius: 6px; padding: 0.6rem 0.75rem; cursor: pointer; transition: border-color 0.15s, box-shadow 0.15s; }
.example-card:hover { border-color: var(--vp-c-brand-1); box-shadow: 0 1px 6px var(--vp-c-brand-soft); }
.example-card-name { font-weight: 700; font-size: 0.85rem; color: var(--vp-c-brand-1); }
.example-card-source { font-size: 0.7rem; color: var(--vp-c-text-3); }
.example-card-desc { font-size: 0.75rem; color: var(--vp-c-text-2); margin: 0.2rem 0; }
.example-card-values { display: block; font-size: 0.68rem; font-family: monospace; background: var(--vp-c-bg-soft); padding: 0.2rem 0.4rem; border-radius: 3px; white-space: pre-wrap; max-height: 60px; overflow: hidden; }

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

@keyframes compareFadeDown {
  from {
    opacity: 0;
    transform: translateY(-8px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@keyframes compareFadeUp {
  from {
    opacity: 0;
    transform: translateY(10px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@keyframes examplesRailEnter {
  from {
    opacity: 0;
    transform: translateX(16px);
  }
  to {
    opacity: 1;
    transform: translateX(0);
  }
}

@keyframes loadingCardEnter {
  from {
    opacity: 0;
    transform: scale(0.98) translateY(8px);
  }
  to {
    opacity: 1;
    transform: scale(1) translateY(0);
  }
}

@keyframes loadingCardPulse {
  0%, 100% {
    box-shadow: 0 16px 42px rgba(0, 0, 0, 0.14);
  }
  50% {
    box-shadow: 0 18px 48px rgba(37, 99, 235, 0.22);
  }
}

@keyframes loadingDot {
  0%, 80%, 100% {
    opacity: 0.35;
    transform: translateY(0);
  }
  40% {
    opacity: 1;
    transform: translateY(-5px);
  }
}

@keyframes loadingProgress {
  0% {
    left: -45%;
  }
  55% {
    left: 100%;
  }
  100% {
    left: 100%;
  }
}

@media (prefers-reduced-motion: reduce) {
  .compare-header,
  .drop-zone,
  .values-section,
  .submit-row,
  .loading-section,
  .loading-card,
  .results-section,
  .examples-panel {
    animation: none;
  }

  .loading-dots span,
  .loading-progress span {
    animation-duration: 2s;
  }
}
</style>
