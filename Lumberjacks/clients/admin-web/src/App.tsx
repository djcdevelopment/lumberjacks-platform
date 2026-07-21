import React, { useEffect, useState, useCallback } from 'react'

interface ServiceStatus {
  service: string
  status: string
}

interface TickInfo {
  current_tick: number
  tick_rate_hz: number
  uptime_seconds: number
  total_players: number
  connected_players: number
  regions: { id: string; player_count: number; active: boolean }[]
}

interface GameEvent {
  event_id: string
  event_type: string
  occurred_at: string
  actor_id: string | null
  region_id: string | null
  source_service: string
}

interface Region {
  id: string
  name: string
  bounds_min: { x: number; y: number; z: number }
  bounds_max: { x: number; y: number; z: number }
  active: boolean
  player_count: number
  tick_rate: number
}

interface Structure {
  id: string
  type: string
  position: { x: number; y: number; z: number }
  rotation: number
  owner_id: string
  region_id: string
  placed_at: string
  tags: string[]
}

interface PlayerProgress {
  player_id: string
  rank: number
  points: number
  updated_at: string
}

interface GuildProgress {
  guild_id: string
  points: number
  challenges_completed: number
  updated_at: string
}

interface TransportDistribution {
  paths: Record<string, number>
  total: number
}

interface AchievementEvidence {
  event_id: string
  event_type: string
  occurred_at: string
}

interface Achievement {
  id: string
  title: string
  description: string
  provenance: 'observed' | 'verified' | 'community_awarded' | 'reconstructed'
  scope: 'community' | 'guild' | 'player'
  unlocked: boolean
  unlocked_at: string | null
  evidence: AchievementEvidence[]
}

interface SessionTransitions {
  active: number
  created: number
  resumed: number
  detached: number
}

const tableStyle = { width: '100%', borderCollapse: 'collapse' as const }
const thStyle = { textAlign: 'left' as const, padding: 8, borderBottom: '1px solid #30363d' }
const tdStyle = { padding: 8, borderBottom: '1px solid #21262d' }
const preStyle = { background: '#161b22', padding: 16, borderRadius: 6, overflow: 'auto' as const }
const subtitleStyle = { color: '#8b949e', marginBottom: 16 }
const badgeUp = { color: '#3fb950', fontWeight: 'bold' as const }
const badgeDown = { color: '#f85149', fontWeight: 'bold' as const }
const cardStyle = {
  background: '#161b22',
  border: '1px solid #30363d',
  borderRadius: 6,
  padding: 16,
  marginBottom: 16,
}
const statValueStyle = { fontSize: 28, fontWeight: 'bold' as const, color: '#58a6ff' }
const statLabelStyle = { fontSize: 12, color: '#8b949e', marginTop: 4 }
const inputStyle = {
  background: '#0d1117',
  border: '1px solid #30363d',
  borderRadius: 6,
  color: '#c9d1d9',
  padding: '6px 10px',
  fontSize: 14,
  width: '100%',
}
const btnStyle = {
  background: '#238636',
  color: '#fff',
  border: 'none',
  borderRadius: 6,
  padding: '8px 16px',
  cursor: 'pointer',
  fontSize: 14,
  fontWeight: 'bold' as const,
}
const btnDangerStyle = {
  ...btnStyle,
  background: 'transparent',
  color: '#f85149',
  border: '1px solid #f85149',
  padding: '4px 10px',
  fontSize: 12,
}

// Provenance tiers are visually distinct and never presented as equivalent
// (dashboard §04): Observed/Reconstructed are auto-computed; Verified and
// Community-awarded are stronger, human/multi-signal claims.
function provenanceBadge(provenance: Achievement['provenance']): {
  label: string
  color: string
  bg: string
  title: string
} {
  switch (provenance) {
    case 'observed':
      return {
        label: 'Observed',
        color: '#58a6ff',
        bg: 'rgba(88,166,255,0.12)',
        title: 'Directly derived from authoritative server events.',
      }
    case 'reconstructed':
      return {
        label: 'Reconstructed',
        color: '#d29922',
        bg: 'rgba(210,153,34,0.12)',
        title: 'Inferred from aggregate/derived data, not a single event.',
      }
    case 'verified':
      return {
        label: 'Verified',
        color: '#3fb950',
        bg: 'rgba(63,185,80,0.12)',
        title: 'Confirmed by an independent second signal (not auto-computed in v1).',
      }
    case 'community_awarded':
      return {
        label: 'Community-awarded',
        color: '#d2a8ff',
        bg: 'rgba(210,168,255,0.12)',
        title: 'Explicitly granted by members/stewards; attributable (not auto-computed in v1).',
      }
  }
}

function formatUptime(seconds: number) {
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  if (h > 0) return `${h}h ${m}m ${s}s`
  if (m > 0) return `${m}m ${s}s`
  return `${s}s`
}

function App() {
  const [activeTab, setActiveTab] = useState<string>('status')
  const [services, setServices] = useState<ServiceStatus[]>([])
  const [tick, setTick] = useState<TickInfo | null>(null)
  const [events, setEvents] = useState<GameEvent[]>([])
  const [regions, setRegions] = useState<Region[]>([])
  const [structures, setStructures] = useState<Structure[]>([])
  const [players, setPlayers] = useState<PlayerProgress[]>([])
  const [guilds, setGuilds] = useState<GuildProgress[]>([])
  const [achievements, setAchievements] = useState<Achievement[]>([])
  const [transport, setTransport] = useState<TransportDistribution | null>(null)
  const [sessions, setSessions] = useState<SessionTransitions | null>(null)

  // Region creation form
  const [newRegion, setNewRegion] = useState({
    name: '',
    id: '',
    minX: '-200', minY: '-10', minZ: '-200',
    maxX: '200', maxY: '100', maxZ: '200',
    tickRate: '20',
  })
  const [regionError, setRegionError] = useState<string | null>(null)

  const fetchTick = useCallback(() => {
    fetch('/api/tick')
      .then((r) => r.json())
      .then((d) => setTick(d))
      .catch(() => setTick(null))
  }, [])

  const fetchRegions = useCallback(() => {
    fetch('/api/regions')
      .then((r) => r.json())
      .then((d) => setRegions(Array.isArray(d) ? d : []))
      .catch(() => setRegions([]))
  }, [])

  const fetchTransport = useCallback(() => {
    fetch('/api/transport')
      .then((r) => r.json())
      .then((d) => setTransport(d))
      .catch(() => setTransport(null))
  }, [])

  const fetchSessions = useCallback(() => {
    fetch('/api/sessions')
      .then((r) => r.json())
      .then((d) => setSessions(d))
      .catch(() => setSessions(null))
  }, [])

  // Load service status + tick on mount
  useEffect(() => {
    fetch('/api/status')
      .then((r) => r.json())
      .then((d) => setServices(d.services || []))
      .catch(() => setServices([]))
    fetchTick()
  }, [fetchTick])

  // Auto-refresh tick every 2 seconds when on status tab
  useEffect(() => {
    if (activeTab !== 'status') return
    const interval = setInterval(fetchTick, 2000)
    return () => clearInterval(interval)
  }, [activeTab, fetchTick])

  // Auto-refresh transport distribution every 2 seconds when on transport tab
  useEffect(() => {
    if (activeTab !== 'transport') return
    fetchTransport()
    const interval = setInterval(fetchTransport, 2000)
    return () => clearInterval(interval)
  }, [activeTab, fetchTransport])

  // Auto-refresh session transitions every 2 seconds when on sessions tab
  useEffect(() => {
    if (activeTab !== 'sessions') return
    fetchSessions()
    const interval = setInterval(fetchSessions, 2000)
    return () => clearInterval(interval)
  }, [activeTab, fetchSessions])

  useEffect(() => {
    if (activeTab === 'events') {
      fetch('/api/events?limit=50')
        .then((r) => r.json())
        .then((d) => setEvents(d.events || []))
        .catch(() => setEvents([]))
    }
    if (activeTab === 'regions') {
      fetchRegions()
    }
    if (activeTab === 'structures') {
      fetch('/api/structures')
        .then((r) => r.json())
        .then((d) => setStructures(Array.isArray(d) ? d : []))
        .catch(() => setStructures([]))
    }
    if (activeTab === 'players') {
      fetch('/api/events?type=structure_placed&limit=100')
        .then((r) => r.json())
        .then((d) => {
          const actorIds = [...new Set((d.events || []).map((e: GameEvent) => e.actor_id).filter(Boolean))]
          return Promise.all(
            actorIds.map((id) =>
              fetch(`/api/players/${id}/progress`)
                .then((r) => (r.ok ? r.json() : null))
                .catch(() => null)
            )
          )
        })
        .then((results) => setPlayers(results.filter(Boolean)))
        .catch(() => setPlayers([]))
    }
    if (activeTab === 'guilds') {
      fetch('/api/guilds')
        .then((r) => r.json())
        .then((d) => setGuilds(Array.isArray(d) ? d : []))
        .catch(() => setGuilds([]))
    }
    if (activeTab === 'achievements') {
      fetch('/api/achievements')
        .then((r) => r.json())
        .then((d) => setAchievements(d.achievements || []))
        .catch(() => setAchievements([]))
    }
  }, [activeTab, fetchRegions])

  const handleCreateRegion = async () => {
    setRegionError(null)
    const body = {
      id: newRegion.id || undefined,
      name: newRegion.name || undefined,
      bounds_min: { x: Number(newRegion.minX), y: Number(newRegion.minY), z: Number(newRegion.minZ) },
      bounds_max: { x: Number(newRegion.maxX), y: Number(newRegion.maxY), z: Number(newRegion.maxZ) },
      tick_rate: Number(newRegion.tickRate),
    }
    try {
      const res = await fetch('/api/regions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      })
      const data = await res.json()
      if (!res.ok) {
        setRegionError(data.error || 'Failed to create region')
        return
      }
      setNewRegion({ name: '', id: '', minX: '-200', minY: '-10', minZ: '-200', maxX: '200', maxY: '100', maxZ: '200', tickRate: '20' })
      fetchRegions()
    } catch {
      setRegionError('Network error')
    }
  }

  const handleDeleteRegion = async (id: string) => {
    try {
      const res = await fetch(`/api/regions/${id}`, { method: 'DELETE' })
      const data = await res.json()
      if (!res.ok) {
        alert(data.error || 'Failed to delete region')
        return
      }
      fetchRegions()
    } catch {
      alert('Network error')
    }
  }

  const sections: { title: string; tabs: string[] }[] = [
    { title: 'Live', tabs: ['status', 'transport', 'sessions', 'regions', 'structures'] },
    { title: 'Safety', tabs: ['safety'] },
    { title: 'History', tabs: ['events', 'players', 'guilds', 'achievements'] },
    { title: 'Trust', tabs: ['trust'] },
  ]

  return (
    <div style={{ display: 'flex', minHeight: '100vh', fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif' }}>
      {/* Sidebar */}
      <nav
        style={{
          width: 200,
          background: '#161b22',
          padding: '20px 0',
          borderRight: '1px solid #30363d',
        }}
      >
        <h2 style={{ padding: '0 16px', marginBottom: 20, fontSize: 14, color: '#8b949e' }}>
          OPERATOR CONSOLE
        </h2>
        {sections.map((section) => (
          <div key={section.title} style={{ marginBottom: 16 }}>
            <div
              style={{
                padding: '0 16px 4px',
                fontSize: 11,
                fontWeight: 'bold',
                letterSpacing: 0.5,
                textTransform: 'uppercase',
                color: '#6e7681',
              }}
            >
              {section.title}
            </div>
            {section.tabs.map((tab) => (
              <button
                key={tab}
                onClick={() => setActiveTab(tab)}
                style={{
                  display: 'block',
                  width: '100%',
                  padding: '8px 16px',
                  background: activeTab === tab ? '#21262d' : 'transparent',
                  border: 'none',
                  color: activeTab === tab ? '#58a6ff' : '#c9d1d9',
                  textAlign: 'left',
                  cursor: 'pointer',
                  fontSize: 14,
                  textTransform: 'capitalize',
                }}
              >
                {tab}
              </button>
            ))}
          </div>
        ))}
      </nav>

      {/* Main content */}
      <main style={{ flex: 1, padding: 24, background: '#0d1117', color: '#c9d1d9' }}>
        <h1 style={{ marginBottom: 16, fontSize: 20 }}>
          {activeTab.charAt(0).toUpperCase() + activeTab.slice(1)}
        </h1>

        {activeTab === 'status' && (
          <div>
            {/* Tick diagnostics */}
            {tick && (
              <div style={{ display: 'flex', gap: 16, marginBottom: 24, flexWrap: 'wrap' }}>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{tick.current_tick.toLocaleString()}</div>
                  <div style={statLabelStyle}>Current Tick</div>
                </div>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{formatUptime(tick.uptime_seconds)}</div>
                  <div style={statLabelStyle}>Uptime</div>
                </div>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{tick.connected_players}</div>
                  <div style={statLabelStyle}>Connected Players</div>
                </div>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{tick.regions.length}</div>
                  <div style={statLabelStyle}>Active Regions</div>
                </div>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{tick.tick_rate_hz} Hz</div>
                  <div style={statLabelStyle}>Tick Rate</div>
                </div>
              </div>
            )}

            {/* Region breakdown */}
            {tick && tick.regions.length > 0 && (
              <div style={{ ...cardStyle, marginBottom: 24 }}>
                <h3 style={{ margin: '0 0 12px', fontSize: 14, color: '#8b949e' }}>Region Load</h3>
                {tick.regions.map((r) => (
                  <div key={r.id} style={{ display: 'flex', justifyContent: 'space-between', padding: '4px 0', borderBottom: '1px solid #21262d' }}>
                    <span>{r.id}</span>
                    <span>
                      <span style={r.active ? badgeUp : badgeDown}>{r.active ? '●' : '○'}</span>
                      {' '}{r.player_count} player{r.player_count !== 1 ? 's' : ''}
                    </span>
                  </div>
                ))}
              </div>
            )}

            <p style={subtitleStyle}>Service health</p>
            <table style={tableStyle}>
              <thead>
                <tr>
                  <th style={thStyle}>Service</th>
                  <th style={thStyle}>Status</th>
                </tr>
              </thead>
              <tbody>
                {services.map((s) => (
                  <tr key={s.service}>
                    <td style={tdStyle}>{s.service}</td>
                    <td style={{ ...tdStyle, ...(s.status === 'up' ? badgeUp : badgeDown) }}>
                      {s.status === 'up' ? '● up' : '○ down'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {activeTab === 'transport' && (
          <div>
            <p style={subtitleStyle}>
              How delivered entity updates split across transports (live, in-process telemetry)
            </p>
            {!transport || transport.total === 0 ? (
              <p>No deliveries recorded yet.</p>
            ) : (
              (() => {
                const order = ['udp', 'binary_ws', 'json_ws']
                const labels: Record<string, string> = {
                  udp: 'UDP',
                  binary_ws: 'Binary WS',
                  json_ws: 'JSON WS (compatibility)',
                }
                const colors: Record<string, string> = {
                  udp: '#58a6ff',
                  binary_ws: '#3fb950',
                  json_ws: '#d29922',
                }
                const total = transport.total
                return (
                  <>
                    <div
                      style={{
                        display: 'flex',
                        width: '100%',
                        height: 32,
                        borderRadius: 6,
                        overflow: 'hidden',
                        marginBottom: 16,
                        background: '#161b22',
                        border: '1px solid #30363d',
                      }}
                    >
                      {order.map((path) => {
                        const count = transport.paths[path] || 0
                        const pct = (count / total) * 100
                        if (count === 0) return null
                        return (
                          <div
                            key={path}
                            title={`${labels[path]}: ${count} (${pct.toFixed(1)}%)`}
                            style={{
                              width: `${pct}%`,
                              background: colors[path],
                              display: 'flex',
                              alignItems: 'center',
                              justifyContent: 'center',
                              fontSize: 11,
                              color: '#0d1117',
                              fontWeight: 'bold',
                              overflow: 'hidden',
                              whiteSpace: 'nowrap',
                            }}
                          >
                            {pct >= 8 ? `${pct.toFixed(0)}%` : ''}
                          </div>
                        )
                      })}
                    </div>
                    <table style={tableStyle}>
                      <thead>
                        <tr>
                          <th style={thStyle}>Transport</th>
                          <th style={thStyle}>Count</th>
                          <th style={thStyle}>Share</th>
                        </tr>
                      </thead>
                      <tbody>
                        {order.map((path) => {
                          const count = transport.paths[path] || 0
                          const pct = total > 0 ? (count / total) * 100 : 0
                          return (
                            <tr key={path}>
                              <td style={tdStyle}>
                                <span
                                  style={{
                                    display: 'inline-block',
                                    width: 10,
                                    height: 10,
                                    borderRadius: 2,
                                    background: colors[path],
                                    marginRight: 8,
                                  }}
                                />
                                {labels[path]}
                              </td>
                              <td style={tdStyle}>{count.toLocaleString()}</td>
                              <td style={{ ...tdStyle, color: '#8b949e' }}>{pct.toFixed(1)}%</td>
                            </tr>
                          )
                        })}
                        <tr>
                          <td style={{ ...tdStyle, fontWeight: 'bold' }}>Total</td>
                          <td style={{ ...tdStyle, fontWeight: 'bold' }}>{total.toLocaleString()}</td>
                          <td style={tdStyle}>—</td>
                        </tr>
                      </tbody>
                    </table>
                  </>
                )
              })()
            )}
          </div>
        )}

        {activeTab === 'sessions' && (
          <div>
            <p style={subtitleStyle}>
              Session joins and leaves (live active count plus cumulative transitions)
            </p>
            {!sessions ? (
              <p>No session data available yet.</p>
            ) : (
              <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{sessions.active.toLocaleString()}</div>
                  <div style={statLabelStyle}>Active</div>
                </div>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{sessions.created.toLocaleString()}</div>
                  <div style={statLabelStyle}>Created</div>
                </div>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{sessions.resumed.toLocaleString()}</div>
                  <div style={statLabelStyle}>Resumed</div>
                </div>
                <div style={{ ...cardStyle, flex: '1 1 140px', minWidth: 140 }}>
                  <div style={statValueStyle}>{sessions.detached.toLocaleString()}</div>
                  <div style={statLabelStyle}>Detached</div>
                </div>
              </div>
            )}
          </div>
        )}

        {activeTab === 'events' && (
          <div>
            <p style={subtitleStyle}>Recent events from the event log ({events.length} shown)</p>
            {events.length === 0 ? (
              <p>No events recorded yet.</p>
            ) : (
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>Type</th>
                    <th style={thStyle}>Actor</th>
                    <th style={thStyle}>Region</th>
                    <th style={thStyle}>Source</th>
                    <th style={thStyle}>Time</th>
                  </tr>
                </thead>
                <tbody>
                  {events.map((e) => (
                    <tr key={e.event_id}>
                      <td style={{ ...tdStyle, color: '#d2a8ff' }}>{e.event_type}</td>
                      <td style={tdStyle}>{e.actor_id ? e.actor_id.slice(0, 8) + '...' : '—'}</td>
                      <td style={tdStyle}>{e.region_id || '—'}</td>
                      <td style={tdStyle}>{e.source_service}</td>
                      <td style={{ ...tdStyle, color: '#8b949e' }}>
                        {new Date(e.occurred_at).toLocaleString()}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        {activeTab === 'regions' && (
          <div>
            <p style={subtitleStyle}>Active world regions</p>

            {/* Create region form */}
            <div style={{ ...cardStyle, marginBottom: 24 }}>
              <h3 style={{ margin: '0 0 12px', fontSize: 14, color: '#8b949e' }}>Create Region</h3>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 12 }}>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Name</label>
                  <input
                    style={inputStyle}
                    placeholder="e.g. The Meadows"
                    value={newRegion.name}
                    onChange={(e) => setNewRegion({ ...newRegion, name: e.target.value })}
                  />
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>ID (optional)</label>
                  <input
                    style={inputStyle}
                    placeholder="auto-generated if empty"
                    value={newRegion.id}
                    onChange={(e) => setNewRegion({ ...newRegion, id: e.target.value })}
                  />
                </div>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr) auto repeat(3, 1fr)', gap: 8, alignItems: 'end', marginBottom: 12 }}>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Min X</label>
                  <input style={inputStyle} type="number" value={newRegion.minX} onChange={(e) => setNewRegion({ ...newRegion, minX: e.target.value })} />
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Min Y</label>
                  <input style={inputStyle} type="number" value={newRegion.minY} onChange={(e) => setNewRegion({ ...newRegion, minY: e.target.value })} />
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Min Z</label>
                  <input style={inputStyle} type="number" value={newRegion.minZ} onChange={(e) => setNewRegion({ ...newRegion, minZ: e.target.value })} />
                </div>
                <div style={{ padding: '0 8px', color: '#8b949e', fontSize: 18, paddingBottom: 8 }}>→</div>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Max X</label>
                  <input style={inputStyle} type="number" value={newRegion.maxX} onChange={(e) => setNewRegion({ ...newRegion, maxX: e.target.value })} />
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Max Y</label>
                  <input style={inputStyle} type="number" value={newRegion.maxY} onChange={(e) => setNewRegion({ ...newRegion, maxY: e.target.value })} />
                </div>
                <div>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Max Z</label>
                  <input style={inputStyle} type="number" value={newRegion.maxZ} onChange={(e) => setNewRegion({ ...newRegion, maxZ: e.target.value })} />
                </div>
              </div>
              <div style={{ display: 'flex', gap: 12, alignItems: 'end' }}>
                <div style={{ width: 120 }}>
                  <label style={{ display: 'block', fontSize: 12, color: '#8b949e', marginBottom: 4 }}>Tick Rate</label>
                  <input style={inputStyle} type="number" value={newRegion.tickRate} onChange={(e) => setNewRegion({ ...newRegion, tickRate: e.target.value })} />
                </div>
                <button style={btnStyle} onClick={handleCreateRegion}>Create Region</button>
              </div>
              {regionError && <p style={{ color: '#f85149', marginTop: 8, fontSize: 13 }}>{regionError}</p>}
            </div>

            {regions.length === 0 ? (
              <p>No regions loaded.</p>
            ) : (
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>Name</th>
                    <th style={thStyle}>ID</th>
                    <th style={thStyle}>Active</th>
                    <th style={thStyle}>Players</th>
                    <th style={thStyle}>Tick Rate</th>
                    <th style={thStyle}>Bounds</th>
                    <th style={thStyle}></th>
                  </tr>
                </thead>
                <tbody>
                  {regions.map((r) => (
                    <tr key={r.id}>
                      <td style={{ ...tdStyle, fontWeight: 'bold' }}>{r.name}</td>
                      <td style={{ ...tdStyle, color: '#8b949e' }}>{r.id}</td>
                      <td style={tdStyle}>
                        <span style={r.active ? badgeUp : badgeDown}>
                          {r.active ? '● active' : '○ inactive'}
                        </span>
                      </td>
                      <td style={tdStyle}>{r.player_count}</td>
                      <td style={tdStyle}>{r.tick_rate} Hz</td>
                      <td style={{ ...tdStyle, fontSize: 12, color: '#8b949e' }}>
                        ({r.bounds_min.x}, {r.bounds_min.y}, {r.bounds_min.z}) →
                        ({r.bounds_max.x}, {r.bounds_max.y}, {r.bounds_max.z})
                      </td>
                      <td style={tdStyle}>
                        {r.id !== 'region-spawn' && (
                          <button
                            style={btnDangerStyle}
                            onClick={() => handleDeleteRegion(r.id)}
                          >
                            Delete
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        {activeTab === 'structures' && (
          <div>
            <p style={subtitleStyle}>Placed structures in the world ({structures.length} total)</p>
            {structures.length === 0 ? (
              <p>No structures placed yet. Connect via WebSocket and send a place_structure message.</p>
            ) : (
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>Type</th>
                    <th style={thStyle}>Position</th>
                    <th style={thStyle}>Owner</th>
                    <th style={thStyle}>Region</th>
                    <th style={thStyle}>Placed At</th>
                    <th style={thStyle}>Tags</th>
                  </tr>
                </thead>
                <tbody>
                  {structures.map((s) => (
                    <tr key={s.id}>
                      <td style={{ ...tdStyle, color: '#7ee787', fontWeight: 'bold' }}>{s.type}</td>
                      <td style={{ ...tdStyle, fontFamily: 'monospace', fontSize: 12 }}>
                        ({s.position.x.toFixed(1)}, {s.position.y.toFixed(1)}, {s.position.z.toFixed(1)})
                      </td>
                      <td style={tdStyle}>{s.owner_id.slice(0, 8)}...</td>
                      <td style={tdStyle}>{s.region_id}</td>
                      <td style={{ ...tdStyle, color: '#8b949e' }}>
                        {new Date(s.placed_at).toLocaleString()}
                      </td>
                      <td style={tdStyle}>{s.tags.length > 0 ? s.tags.join(', ') : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        {activeTab === 'players' && (
          <div>
            <p style={subtitleStyle}>Player progression</p>
            {players.length === 0 ? (
              <p>No player progress recorded yet.</p>
            ) : (
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>Player ID</th>
                    <th style={thStyle}>Rank</th>
                    <th style={thStyle}>Points</th>
                    <th style={thStyle}>Last Updated</th>
                  </tr>
                </thead>
                <tbody>
                  {players.map((p) => (
                    <tr key={p.player_id}>
                      <td style={{ ...tdStyle, fontFamily: 'monospace' }}>{p.player_id.slice(0, 12)}...</td>
                      <td style={tdStyle}>{p.rank}</td>
                      <td style={{ ...tdStyle, color: '#d2a8ff', fontWeight: 'bold' }}>{p.points}</td>
                      <td style={{ ...tdStyle, color: '#8b949e' }}>
                        {new Date(p.updated_at).toLocaleString()}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        {activeTab === 'guilds' && (
          <div>
            <p style={subtitleStyle}>Guild progress</p>
            {guilds.length === 0 ? (
              <p>No guild progress recorded yet.</p>
            ) : (
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>Guild ID</th>
                    <th style={thStyle}>Points</th>
                    <th style={thStyle}>Challenges</th>
                    <th style={thStyle}>Last Updated</th>
                  </tr>
                </thead>
                <tbody>
                  {guilds.map((g) => (
                    <tr key={g.guild_id}>
                      <td style={{ ...tdStyle, fontFamily: 'monospace' }}>{g.guild_id}</td>
                      <td style={{ ...tdStyle, color: '#d2a8ff', fontWeight: 'bold' }}>{g.points}</td>
                      <td style={tdStyle}>{g.challenges_completed}</td>
                      <td style={{ ...tdStyle, color: '#8b949e' }}>
                        {new Date(g.updated_at).toLocaleString()}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        {activeTab === 'achievements' && (
          <div>
            <p style={subtitleStyle}>
              What this community has accomplished together. Each achievement is a projection
              over authoritative events — never a manually-asserted badge — and carries a
              provenance tier. Only Observed and Reconstructed unlock automatically; Verified
              and Community-awarded require confirmation or human authority and are not equivalent.
            </p>
            {achievements.length === 0 ? (
              <p>No achievements available yet.</p>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                {achievements.map((a) => {
                  const badge = provenanceBadge(a.provenance)
                  return (
                    <div
                      key={a.id}
                      style={{
                        ...cardStyle,
                        marginBottom: 0,
                        opacity: a.unlocked ? 1 : 0.55,
                        borderLeft: `3px solid ${a.unlocked ? '#3fb950' : '#30363d'}`,
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
                        <span style={{ fontSize: 15, fontWeight: 'bold', color: '#c9d1d9' }}>
                          {a.title}
                        </span>
                        <span
                          title={badge.title}
                          style={{
                            fontSize: 11,
                            fontWeight: 'bold',
                            textTransform: 'uppercase',
                            letterSpacing: 0.5,
                            padding: '2px 8px',
                            borderRadius: 10,
                            color: badge.color,
                            background: badge.bg,
                            border: `1px solid ${badge.color}`,
                          }}
                        >
                          {badge.label}
                        </span>
                        <span style={{ fontSize: 11, color: '#8b949e', textTransform: 'capitalize' }}>
                          {a.scope}
                        </span>
                        <span
                          style={{
                            marginLeft: 'auto',
                            ...(a.unlocked ? badgeUp : badgeDown),
                            fontSize: 12,
                          }}
                        >
                          {a.unlocked ? '● unlocked' : '○ locked'}
                        </span>
                      </div>
                      <p style={{ margin: '8px 0 0', color: '#8b949e', fontSize: 13.5, lineHeight: 1.5 }}>
                        {a.description}
                      </p>
                      {a.unlocked && (
                        <div style={{ marginTop: 10, fontSize: 12, color: '#6e7681' }}>
                          {a.unlocked_at && (
                            <div>Unlocked {new Date(a.unlocked_at).toLocaleString()}</div>
                          )}
                          {a.evidence.length > 0 && (
                            <div style={{ marginTop: 4 }}>
                              <span style={{ color: '#8b949e' }}>Evidence: </span>
                              {a.evidence.map((ev, i) => (
                                <span key={ev.event_id} style={{ fontFamily: 'monospace' }}>
                                  {i > 0 ? ', ' : ''}
                                  {ev.event_type} ({ev.event_id.slice(0, 8)})
                                </span>
                              ))}
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        )}

        {activeTab === 'safety' && (
          <div>
            <p style={subtitleStyle}>Backup and restore integrity signals</p>
            <div style={cardStyle}>
              <p style={{ margin: 0, lineHeight: 1.5 }}>
                Safety signals (backups, verified restore point) are not yet instrumented — see
                docs/dashboard strategy backlog item D-08.
              </p>
            </div>
          </div>
        )}

        {activeTab === 'trust' && (
          <div>
            <p style={subtitleStyle}>Roles, access, and attributable actions</p>
            <div style={cardStyle}>
              <p style={{ margin: 0, lineHeight: 1.5 }}>
                Trust signals (roles, access, attributable actions) require an auth/role model that
                does not exist yet — see backlog D-09.
              </p>
            </div>
          </div>
        )}
      </main>
    </div>
  )
}

export { App }
