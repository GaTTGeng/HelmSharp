---
title: Helm Compare
sidebar: false
navbar: true
aside: false
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
  max-width: none !important;
  margin: 0 auto !important;
}
.VPDoc:not(.has-sidebar) .content {
  max-width: 100% !important;
}
.VPDoc:not(.has-sidebar) .content-container {
  max-width: none !important;
}
</style>
