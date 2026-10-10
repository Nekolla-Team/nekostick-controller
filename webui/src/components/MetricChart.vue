<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([LineChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer])

interface MetricSeries {
  name: string
  color: string
  data: Array<number | null>
}

const props = withDefaults(
  defineProps<{
    /** Same length as every series' data: x labels for each sample. */
    times: string[]
    series: MetricSeries[]
    /** Formats raw values for the y axis and tooltip. */
    formatValue: (value: number) => string
    /** Pin the y axis to 0-100 (CPU charts). */
    percentAxis?: boolean
    height?: string
  }>(),
  { percentAxis: false, height: '220px' },
)

const chartEl = ref<HTMLDivElement | null>(null)
let chart: echarts.ECharts | null = null
let resizeObserver: ResizeObserver | null = null

function buildOption(): echarts.EChartsCoreOption {
  return {
    animation: false,
    grid: { bottom: 28, left: 8, right: 12, top: 16, containLabel: true },
    legend: {
      bottom: 0,
      icon: 'roundRect',
      itemHeight: 4,
      itemWidth: 14,
      show: props.series.length > 1,
      textStyle: { color: 'rgba(128, 128, 128, 0.9)', fontSize: 11 },
    },
    series: props.series.map((s) => ({
      areaStyle: { opacity: 0.12 },
      connectNulls: true,
      data: s.data,
      emphasis: { disabled: true },
      itemStyle: { color: s.color },
      lineStyle: { color: s.color, width: 2 },
      name: s.name,
      showSymbol: false,
      smooth: true,
      type: 'line' as const,
    })),
    tooltip: {
      trigger: 'axis',
      valueFormatter: (value: unknown) =>
        typeof value === 'number' ? props.formatValue(value) : 'N/A',
    },
    xAxis: {
      axisLabel: { color: 'rgba(128, 128, 128, 0.7)', fontSize: 10 },
      axisLine: { lineStyle: { color: 'rgba(128, 128, 128, 0.3)' } },
      axisTick: { show: false },
      boundaryGap: false,
      data: props.times,
      type: 'category' as const,
    },
    yAxis: {
      axisLabel: {
        color: 'rgba(128, 128, 128, 0.7)',
        fontSize: 10,
        formatter: (value: number) => props.formatValue(value),
      },
      max: props.percentAxis ? 100 : undefined,
      min: props.percentAxis ? 0 : undefined,
      splitLine: { lineStyle: { color: 'rgba(128, 128, 128, 0.14)', type: 'dashed' as const } },
      type: 'value' as const,
    },
  }
}

watch(
  () => [props.times, props.series] as const,
  () => {
    chart?.setOption(buildOption())
  },
)

onMounted(() => {
  if (chartEl.value === null) return
  chart = echarts.init(chartEl.value, null, { renderer: 'canvas' })
  chart.setOption(buildOption())
  resizeObserver = new ResizeObserver(() => chart?.resize())
  resizeObserver.observe(chartEl.value)
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  chart?.dispose()
  chart = null
})
</script>

<template>
  <div ref="chartEl" class="metric-chart" :style="{ height }" />
</template>

<style scoped>
.metric-chart {
  width: 100%;
}
</style>
