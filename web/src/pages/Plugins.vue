<template>
  <div class="plugins-page">
    <div class="card">
      <div class="card-title">
        已安装插件
        <div class="actions">
          <button class="btn" @click="restartGateway">重启</button>
        </div>
      </div>
      <div class="card-body">
        <div v-if="installed.length === 0" class="empty">暂无已安装插件</div>
        <div class="list">
          <div v-for="p in installed" :key="p.identifier || p.name" class="row">
            <div class="info">
              <div class="name">{{ p.name }}</div>
              <div class="version">{{ p.version }}</div>
              <div class="status" v-if="p.waitingRestart">已卸载，等待重启</div>
            </div>
            <div class="actions">
              <button class="btn danger" @click="uninstallPlugin(p.identifier)">卸载</button>
            </div>
          </div>
        </div>
      </div>
    </div>

  <div class="card">
      <div class="card-title">插件列表</div>
      <div class="card-body">
        <div v-if="plugins.length === 0" class="empty">暂无插件数据</div>
        <div class="grid">
          <div v-for="p in plugins" :key="p.id || p.name" class="tile">
            <img v-if="p.logoUrl" :src="p.logoUrl" alt="logo" class="tile-logo" />
            <div class="tile-name">{{ p.name }}</div>
            <div class="tile-desc">{{ p.description }}</div>
            <div class="tile-actions">
              <button class="btn" @click="installPlugin(p)" :disabled="installBusy">安装</button>
              <button class="btn" v-if="shouldShowUpdate(p)" @click="updatePlugin(p)" :disabled="installBusy">更新</button>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import appConfig from '../config/app.js'
const installed = ref([])
const plugins = ref([])
let socket
const installBusy = ref(false)
const installStates = ref({})
function requestInstalled() {
  if (!socket || socket.readyState !== 1) return
  try { socket.send(JSON.stringify({ type: 'list_installed_plugins' })) } catch {}
}
async function fetchPlugins() {
  try {
    const res = await fetch('https://api.codexus.today/api/components/get/all?offset=0&limit=20')
    const data = await res.json()
    const items = Array.isArray(data?.items) ? data.items : []
    plugins.value = (items || []).map(it => ({
      id: it.id,
      name: it.name || '',
      description: it.shortDescription || '',
      logoUrl: (it.logoUrl || '').replace(/`/g, '').trim(),
      type: it.type || '',
      publisher: it.publisher || '',
      score: typeof it.score === 'number' ? it.score : 0,
      downloadCount: typeof it.downloadCount === 'number' ? it.downloadCount : 0,
      isOfficial: !!it.isOfficial
    }))
    for (const p of plugins.value) {
      try {
        const r = await fetch('https://api.codexus.today/api/components/get/detail?id=' + encodeURIComponent(p.id))
        const d = await r.json()
        const info = d && d.items ? d : null
        if (info && Array.isArray(info.items)) {
          p.installInfo = { items: info.items.map(x => ({ id: x.id || p.id, downloadUrl: x.downloadUrl, fileHash: x.fileHash, version: x.version })) }
          p.version = info.version || (info.items[0] && info.items[0].version) || ''
        }
      } catch {}
    }
  } catch {}
}
function requestInstallStateForAll() {
  if (!socket || socket.readyState !== 1) return
  for (const p of plugins.value) {
    try { socket.send(JSON.stringify({ type: 'query_plugin_install', pluginId: p.id, pluginVersion: p.version || 'any' })) } catch {}
  }
}
function uninstallPlugin(id) {
  if (!id) return
  if (!socket || socket.readyState !== 1) return
  try { socket.send(JSON.stringify({ type: 'uninstall_plugin', pluginId: id })) } catch {}
}
function restartGateway() {
  if (!socket || socket.readyState !== 1) return
  try { socket.send(JSON.stringify({ type: 'restart' })) } catch {}
}
function shouldShowUpdate(p) {
  const st = installStates.value[p.id]
  if (!st) return false
  if (!p.version) return false
  if (!st.installedVersion) return false
  return p.version !== st.installedVersion
}
function installPlugin(p) {
  if (!socket || socket.readyState !== 1) return
  const info = p.installInfo || null
  installBusy.value = true
  if (info) {
    try { socket.send(JSON.stringify({ type: 'install_plugin', id: p.id, info, loadNow: true })) } catch {}
  } else {
    try { socket.send(JSON.stringify({ type: 'install_plugin', id: p.id })) } catch {}
  }
  setTimeout(() => { installBusy.value = false }, 1000)
}
function updatePlugin(p) {
  if (!socket || socket.readyState !== 1) return
  const st = installStates.value[p.id]
  const payload = { id: p.id, old: (st && st.installedVersion) || '', info: p.installInfo || null }
  installBusy.value = true
  try { socket.send(JSON.stringify({ type: 'update_plugin', payload })) } catch {}
  setTimeout(() => { installBusy.value = false }, 1000)
}
onMounted(() => {
  try {
    socket = new WebSocket(appConfig.getWsUrl())
    socket.onopen = () => { requestInstalled(); fetchPlugins().then(() => requestInstallStateForAll()) }
    socket.onmessage = (e) => {
      let msg
      try { msg = JSON.parse(e.data) } catch { msg = null }
      if (!msg || !msg.type) return
      if (msg.type === 'installed_plugins' && Array.isArray(msg.items)) {
        installed.value = (msg.items || []).map(it => ({
          identifier: it.identifier,
          name: it.name,
          version: it.version,
          waitingRestart: !!it.waitingRestart
        }))
      } else if (msg.type === 'query_plugin_install') {
        if (msg.pluginId) {
          installStates.value[msg.pluginId] = { installed: !!msg.pluginIsInstalled, installedVersion: msg.pluginInstalledVersion || '' }
        }
      } else if (msg.type === 'installed_plugins_updated') {
        requestInstalled()
      }
    }
  } catch {}
})
onUnmounted(() => { try { if (socket && socket.readyState === 1) socket.close() } catch {} })
</script>

<style scoped>
.plugins-page { display: flex; flex-direction: column; gap: 16px; width: 100%; align-self: flex-start; margin-right: auto; }
.card { border: 1px solid var(--color-border); border-radius: 12px; background: var(--color-background); color: var(--color-text); }
.card-title { display: flex; align-items: center; justify-content: space-between; padding: 12px 16px; font-size: 16px; font-weight: 600; border-bottom: 1px solid var(--color-border); }
.card-body { padding: 12px 16px; }
.empty { opacity: 0.7; }
.list { display: grid; grid-template-columns: 1fr; gap: 8px; }
.row { display: flex; align-items: center; justify-content: space-between; border: 1px solid var(--color-border); border-radius: 12px; padding: 12px; }
.actions { display: flex; gap: 8px; }
.btn { padding: 8px 12px; border: 1px solid var(--color-border); background: var(--color-background); color: var(--color-text); border-radius: 8px; cursor: pointer; transition: background-color 200ms ease, border-color 200ms ease, transform 100ms ease; }
.btn:hover { background: var(--color-background-mute); border-color: var(--color-border-hover); }
.btn:active { transform: scale(0.98); }
.btn.danger { color: #ef4444; }
.status { font-size: 12px; color: #f59e0b; }
.info { display: flex; flex-direction: column; gap: 4px; }
.name { font-size: 14px; font-weight: 600; }
.version { font-size: 12px; opacity: 0.7; }
.grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; }
.tile { border: 1px solid var(--color-border); border-radius: 12px; padding: 12px; background: var(--color-background-soft); }
.tile-logo { width: 48px; height: 48px; object-fit: contain; margin-bottom: 8px; }
.tile-name { font-size: 14px; font-weight: 600; }
.tile-desc { font-size: 12px; opacity: 0.8; margin-top: 4px; }
.tile-actions { display: flex; gap: 8px; margin-top: 8px; }
</style>