---
title: Helm Compare
sidebar: false
navbar: true
prev: false
next: false
---

<script setup>
import HelmCompare from './.vitepress/theme/components/HelmCompare.vue'
</script>

<ClientOnly>
  <HelmCompare />
</ClientOnly>

<style>
/* When sidebar is hidden, VitePress shifts content left — recenter it */
.VPDoc:not(.has-sidebar) .container {
  margin: 0 auto !important;
}
.VPDoc:not(.has-sidebar) .content {
  max-width: 100% !important;
}
</style>
