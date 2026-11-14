import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Badge,
  Button,
  Dropdown,
  Input,
  Option,
  Tab,
  TabList,
  Textarea,
  Text,
  tokens
} from '@fluentui/react-components';
import {
  Background,
  Connection,
  Controls,
  Edge,
  Handle,
  MarkerType,
  Node,
  NodeProps,
  Position,
  ReactFlow
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import {
  BoardStatus,
  GeneratedPlan,
  ItemSize,
  PlannedBacklogItem,
  PriorityLevel
} from '../../api/client';

interface PlanCanvasEditorProps {
  plan: GeneratedPlan;
  onChange: (next: GeneratedPlan) => void;
}

interface PlanNodeData extends Record<string, unknown> {
  item: PlannedBacklogItem;
  selected: boolean;
  stage: number;
  onSelect: (key: string) => void;
}

const EFFORT_LABELS: Record<ItemSize, { short: string; detail: string }> = {
  Small: { short: 'Small', detail: 'Small — Focused single slice (~1 Copilot session)' },
  Medium: { short: 'Medium', detail: 'Medium — Standard feature slice (~2 sessions)' },
  Large: { short: 'Large', detail: 'Large — Multi-component slice (~3+ sessions)' },
  XLarge: { short: 'XLarge', detail: 'XLarge — Oversized scope (consider splitting)' }
};

const PRIORITY_LABELS: Record<PriorityLevel, { short: string; detail: string; color: string }> = {
  Critical: { short: 'P0 Critical', detail: 'Critical (P0) — Dispatched first when unblocked', color: '#dc2626' },
  High: { short: 'P1 High', detail: 'High (P1) — Core user-journey slice', color: '#d97706' },
  Medium: { short: 'P2 Medium', detail: 'Medium (P2) — Standard backlog order', color: '#2563eb' },
  Low: { short: 'P3 Low', detail: 'Low (P3) — Polish / nice-to-have', color: '#6b7280' }
};

function computeTopologicalStages(items: PlannedBacklogItem[]): Map<string, number> {
  const itemMap = new Map(items.map(i => [i.key, i]));
  const memo = new Map<string, number>();
  const visiting = new Set<string>();

  const getDepth = (key: string): number => {
    if (memo.has(key)) return memo.get(key)!;
    if (visiting.has(key)) return 0; // Guard against accidental cycle
    visiting.add(key);
    const it = itemMap.get(key);
    let maxParent = -1;
    if (it) {
      for (const dep of it.blockedByKeys) {
        if (itemMap.has(dep)) {
          maxParent = Math.max(maxParent, getDepth(dep));
        }
      }
    }
    visiting.delete(key);
    const depth = maxParent + 1;
    memo.set(key, depth);
    return depth;
  };

  for (const it of items) {
    getDepth(it.key);
  }
  return memo;
}

function createsCycle(items: PlannedBacklogItem[], blockerKey: string, targetKey: string): boolean {
  if (blockerKey === targetKey) return true;
  const itemMap = new Map(items.map(i => [i.key, i]));
  const visited = new Set<string>();
  const stack = [blockerKey];
  while (stack.length > 0) {
    const cur = stack.pop()!;
    if (cur === targetKey) return true;
    if (visited.has(cur)) continue;
    visited.add(cur);
    const node = itemMap.get(cur);
    if (node) {
      for (const dep of node.blockedByKeys) {
        stack.push(dep);
      }
    }
  }
  return false;
}

const PlanWorkItemNode: React.FC<NodeProps<Node<PlanNodeData>>> = ({ data }) => {
  const { item, selected, onSelect } = data;
  const isAt = item.isAcceptanceTest;
  const accentColor = isAt ? '#8b5cf6' : '#2563eb';
  const prioMeta = PRIORITY_LABELS[item.priority] ?? PRIORITY_LABELS.Medium;
  const hasWarning = item.acceptanceCriteria.length === 0 || item.size === 'XLarge';

  return (
    <div
      onClick={() => onSelect(item.key)}
      style={{
        width: 276,
        padding: '10px 12px',
        borderRadius: 10,
        cursor: 'pointer',
        background: tokens.colorNeutralBackground1,
        color: tokens.colorNeutralForeground1,
        border: selected ? `2.5px solid ${accentColor}` : `1.5px solid ${tokens.colorNeutralStroke1}`,
        boxShadow: selected
          ? `0 0 0 3px ${isAt ? 'rgba(139, 92, 246, 0.24)' : 'rgba(37, 99, 235, 0.24)'}, 0 6px 16px rgba(0,0,0,0.14)`
          : '0 2px 6px rgba(0,0,0,0.08)',
        display: 'flex',
        flexDirection: 'column',
        gap: 7,
        transition: 'box-shadow 120ms ease, border-color 120ms ease'
      }}
    >
      <Handle
        type="target"
        position={Position.Left}
        style={{ width: 10, height: 10, background: accentColor, border: '2px solid #fff' }}
      />

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 6 }}>
        <span
          style={{
            fontSize: 11,
            fontWeight: 700,
            padding: '2px 7px',
            borderRadius: 999,
            background: isAt ? 'rgba(139, 92, 246, 0.16)' : 'rgba(37, 99, 235, 0.15)',
            color: accentColor
          }}
        >
          {item.key} · {isAt ? 'Acceptance Test' : 'Implementation'}
        </span>
        {hasWarning && (
          <span style={{ fontSize: 11, fontWeight: 600, color: tokens.colorPaletteRedForeground1 }}>
            ⚠ Review
          </span>
        )}
      </div>

      <div
        style={{
          fontSize: 13,
          fontWeight: 600,
          lineHeight: 1.35,
          wordBreak: 'break-word',
          display: '-webkit-box',
          WebkitLineClamp: 3,
          WebkitBoxOrient: 'vertical',
          overflow: 'hidden'
        }}
      >
        {item.title || '(Untitled work item)'}
      </div>

      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5, alignItems: 'center' }}>
        <span
          title="Estimated coding scope"
          style={{
            fontSize: 11,
            padding: '1px 6px',
            borderRadius: 4,
            background: tokens.colorNeutralBackground3,
            color: tokens.colorNeutralForeground2
          }}
        >
          Effort: <strong>{item.size}</strong>
        </span>
        <span
          title="Scheduler dispatch priority"
          style={{
            fontSize: 11,
            padding: '1px 6px',
            borderRadius: 4,
            background: tokens.colorNeutralBackground3,
            color: prioMeta.color,
            fontWeight: 600
          }}
        >
          {prioMeta.short}
        </span>
        <span style={{ fontSize: 11, color: tokens.colorNeutralForeground3, marginLeft: 'auto' }}>
          ✓ {item.acceptanceCriteria.length} AC
        </span>
      </div>

      {item.requirementIds.length > 0 && (
        <div style={{ fontSize: 10.5, color: tokens.colorNeutralForeground3 }}>
          Covers: {item.requirementIds.join(', ')}
        </div>
      )}

      <Handle
        type="source"
        position={Position.Right}
        style={{ width: 10, height: 10, background: accentColor, border: '2px solid #fff' }}
      />
    </div>
  );
};

const nodeTypes = { planItem: PlanWorkItemNode };

export const PlanCanvasEditor: React.FC<PlanCanvasEditorProps> = ({ plan, onChange }) => {
  const [viewTab, setViewTab] = useState<'canvas' | 'adr'>('canvas');
  const [selectedKey, setSelectedKey] = useState<string>(plan.items[0]?.key ?? '');
  const [cycleNotice, setCycleNotice] = useState<string | null>(null);

  useEffect(() => {
    if (plan.items.length === 0) {
      setSelectedKey('');
      return;
    }
    if (!plan.items.some(i => i.key === selectedKey)) {
      setSelectedKey(plan.items[0].key);
    }
  }, [plan.items, selectedKey]);

  const recalcForecast = useCallback(
    (items: PlannedBacklogItem[]): GeneratedPlan => ({
      ...plan,
      items,
      estimatedCostUsd: Number((items.length * 0.65).toFixed(2)),
      estimatedDurationMinutes: items.length * 14
    }),
    [plan]
  );

  const selectedIndex = plan.items.findIndex(i => i.key === selectedKey);
  const selectedItem = selectedIndex >= 0 ? plan.items[selectedIndex] : null;

  const updateSelectedItem = (patch: Partial<PlannedBacklogItem>) => {
    if (selectedIndex < 0) return;
    const items = plan.items.map((it, idx) => (idx === selectedIndex ? { ...it, ...patch } : it));
    onChange(recalcForecast(items));
  };

  const addWorkItem = (isAcceptanceTest = false) => {
    const prefix = isAcceptanceTest ? 'AT' : 'ISS';
    let counter = plan.items.length + 1;
    let candidate = `${prefix}-${isAcceptanceTest ? String(counter).padStart(3, '0') : counter}`;
    const existingKeys = new Set(plan.items.map(i => i.key));
    while (existingKeys.has(candidate)) {
      counter++;
      candidate = `${prefix}-${isAcceptanceTest ? String(counter).padStart(3, '0') : counter}`;
    }

    const newItem: PlannedBacklogItem = {
      key: candidate,
      title: isAcceptanceTest
        ? 'Acceptance tests: Verify new user journey'
        : 'Implement vertical feature slice',
      description: 'Describe the user-visible behavior and technical scope for this slice.',
      acceptanceCriteria: ['Automated build and tests pass cleanly'],
      requirementIds: plan.requirements[0] ? [plan.requirements[0].id] : [],
      size: 'Small',
      priority: 'Medium',
      milestone: plan.milestones[0] ?? 'MVP',
      isAcceptanceTest,
      blockedByKeys: selectedItem ? [selectedItem.key] : [],
      blockedByExistingIssueNumbers: [],
      predictedTouchPaths: [],
      initialStatus: 'Ready'
    };

    const nextItems = [...plan.items, newItem];
    onChange(recalcForecast(nextItems));
    setSelectedKey(newItem.key);
  };

  const deleteSelectedItem = () => {
    if (!selectedItem) return;
    const removedKey = selectedItem.key;
    const nextItems = plan.items
      .filter(it => it.key !== removedKey)
      .map(it => ({ ...it, blockedByKeys: it.blockedByKeys.filter(k => k !== removedKey) }));
    onChange(recalcForecast(nextItems));
    setSelectedKey(nextItems[0]?.key ?? '');
  };

  const handleConnect = useCallback(
    (connection: Connection) => {
      const source = connection.source;
      const target = connection.target;
      if (!source || !target || source === target) return;

      if (createsCycle(plan.items, source, target)) {
        setCycleNotice(`Cannot link ${source} → ${target}: that dependency would create a cycle.`);
        return;
      }

      setCycleNotice(null);
      const nextItems = plan.items.map(it => {
        if (it.key !== target) return it;
        if (it.blockedByKeys.includes(source)) return it;
        return { ...it, blockedByKeys: [...it.blockedByKeys, source] };
      });
      onChange(recalcForecast(nextItems));
      setSelectedKey(target);
    },
    [onChange, plan.items, recalcForecast]
  );

  const toggleDependency = (blockerKey: string, add: boolean) => {
    if (!selectedItem) return;
    if (add && createsCycle(plan.items, blockerKey, selectedItem.key)) {
      setCycleNotice(`Cannot add ${blockerKey} as a blocker of ${selectedItem.key}: that would create a cycle.`);
      return;
    }
    setCycleNotice(null);
    const nextBlockedBy = add
      ? Array.from(new Set([...selectedItem.blockedByKeys, blockerKey]))
      : selectedItem.blockedByKeys.filter(k => k !== blockerKey);
    updateSelectedItem({ blockedByKeys: nextBlockedBy });
  };

  const graph = useMemo(() => {
    const stages = computeTopologicalStages(plan.items);
    const columns = new Map<number, PlannedBacklogItem[]>();
    for (const it of plan.items) {
      const st = stages.get(it.key) ?? 0;
      const arr = columns.get(st) ?? [];
      arr.push(it);
      columns.set(st, arr);
    }

    const colWidth = 345;
    const rowHeight = 162;

    const nodes: Node<PlanNodeData>[] = [];
    for (const [stage, colItems] of Array.from(columns.entries()).sort((a, b) => a[0] - b[0])) {
      colItems.forEach((it, rowIdx) => {
        nodes.push({
          id: it.key,
          type: 'planItem',
          position: {
            x: stage * colWidth + 24,
            y: rowIdx * rowHeight + 28
          },
          data: {
            item: it,
            selected: it.key === selectedKey,
            stage,
            onSelect: setSelectedKey
          }
        });
      });
    }

    const edges: Edge[] = plan.items.flatMap(it =>
      it.blockedByKeys.map(blocker => {
        const isSelectedEdge = blocker === selectedKey || it.key === selectedKey;
        return {
          id: `${blocker}->${it.key}`,
          source: blocker,
          target: it.key,
          animated: isSelectedEdge,
          style: {
            stroke: isSelectedEdge ? '#2563eb' : tokens.colorNeutralStroke1,
            strokeWidth: isSelectedEdge ? 2.4 : 1.6
          },
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color: isSelectedEdge ? '#2563eb' : undefined
          }
        };
      })
    );

    return { nodes, edges, stageCount: columns.size };
  }, [plan.items, selectedKey]);

  const availableBlockers = useMemo(() => {
    if (!selectedItem) return [];
    return plan.items.filter(
      cand =>
        cand.key !== selectedItem.key &&
        !selectedItem.blockedByKeys.includes(cand.key) &&
        !createsCycle(plan.items, cand.key, selectedItem.key)
    );
  }, [plan.items, selectedItem]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      {/* Top Toolbar: Tabs + Clear Legend + Add Node Actions */}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          flexWrap: 'wrap',
          gap: 10,
          padding: '8px 12px',
          borderRadius: 8,
          background: tokens.colorNeutralBackground2
        }}
      >
        <TabList
          size="small"
          selectedValue={viewTab}
          onTabSelect={(_, d) => setViewTab(d.value as 'canvas' | 'adr')}
        >
          <Tab value="canvas">Interactive Dependency Canvas ({plan.items.length})</Tab>
          <Tab value="adr">Architecture (ADR-0001) & Requirements ({plan.requirements.length})</Tab>
        </TabList>

        {viewTab === 'canvas' && (
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              Click any card to edit · Drag right-handle → left-handle to add a dependency
            </Text>
            <Button size="small" appearance="secondary" onClick={() => addWorkItem(false)}>
              + Add Feature Slice
            </Button>
            <Button size="small" appearance="outline" onClick={() => addWorkItem(true)}>
              + Add Acceptance Test
            </Button>
          </div>
        )}
      </div>

      {/* Plain-English Legend Bar */}
      {viewTab === 'canvas' && (
        <div
          style={{
            display: 'flex',
            flexWrap: 'wrap',
            gap: 14,
            alignItems: 'center',
            padding: '8px 12px',
            borderRadius: 8,
            border: `1px solid ${tokens.colorNeutralStroke2}`,
            fontSize: 12,
            color: tokens.colorNeutralForeground2
          }}
        >
          <span>
            <strong style={{ color: '#2563eb' }}>● Implementation</strong>: Feature / skeleton PR written by Copilot
          </span>
          <span>
            <strong style={{ color: '#8b5cf6' }}>● Acceptance Test</strong>: Locked test suite merged before implementation
          </span>
          <span>
            <strong>A → B</strong>: A must merge before Copilot starts B
          </span>
          <span>
            <strong>Effort</strong> (Small–XLarge): Estimated coding scope per issue
          </span>
          <span>
            <strong>P0–P3 Priority</strong>: Order Copilot picks ready items
          </span>
        </div>
      )}

      {cycleNotice && (
        <div
          style={{
            padding: '8px 12px',
            borderRadius: 6,
            background: tokens.colorPaletteYellowBackground1,
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center'
          }}
        >
          <Text size={200} weight="semibold">
            {cycleNotice}
          </Text>
          <Button size="small" appearance="subtle" onClick={() => setCycleNotice(null)}>
            Dismiss
          </Button>
        </div>
      )}

      {viewTab === 'adr' ? (
        <div style={{ display: 'grid', gridTemplateColumns: 'minmax(320px, 1.4fr) minmax(260px, 1fr)', gap: 14 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <Text weight="semibold">ADR-0001 Architecture Decision Markdown</Text>
            <Textarea
              rows={18}
              value={plan.adr0001Markdown}
              onChange={(_, d) => onChange({ ...plan, adr0001Markdown: d.value })}
              style={{ fontFamily: 'monospace', fontSize: 12.5 }}
            />
          </div>
          <div
            style={{
              padding: 14,
              borderRadius: 8,
              background: tokens.colorNeutralBackground2,
              display: 'flex',
              flexDirection: 'column',
              gap: 8,
              maxHeight: 460,
              overflowY: 'auto'
            }}
          >
            <Text weight="semibold">Extracted Requirements ({plan.requirements.length})</Text>
            {plan.requirements.map(r => (
              <div
                key={r.id}
                style={{
                  padding: '8px 10px',
                  borderRadius: 6,
                  background: tokens.colorNeutralBackground1,
                  border: `1px solid ${tokens.colorNeutralStroke2}`
                }}
              >
                <Badge appearance="tint" color="brand" style={{ marginBottom: 4 }}>
                  {r.id}
                </Badge>
                <Text size={200} block>
                  {r.text}
                </Text>
              </div>
            ))}
          </div>
        </div>
      ) : (
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'minmax(440px, 1.45fr) minmax(360px, 1fr)',
            gap: 14,
            alignItems: 'stretch'
          }}
        >
          {/* Left: Interactive React Flow Dependency Graph Canvas */}
          <div
            style={{
              height: 580,
              borderRadius: 10,
              overflow: 'hidden',
              border: `1px solid ${tokens.colorNeutralStroke2}`,
              background: tokens.colorNeutralBackground2
            }}
          >
            <ReactFlow
              nodes={graph.nodes}
              edges={graph.edges}
              nodeTypes={nodeTypes}
              onConnect={handleConnect}
              onNodeClick={(_, node) => setSelectedKey(node.id)}
              fitView
              fitViewOptions={{ padding: 0.18 }}
              minZoom={0.35}
              maxZoom={1.5}
              proOptions={{ hideAttribution: true }}
            >
              <Background gap={18} size={1} />
              <Controls showInteractive={false} />
            </ReactFlow>
          </div>

          {/* Right: Readable Contextual Inspector for Selected Node */}
          <div
            style={{
              height: 580,
              overflowY: 'auto',
              padding: 16,
              borderRadius: 10,
              border: `1px solid ${tokens.colorNeutralStroke2}`,
              background: tokens.colorNeutralBackground2,
              display: 'flex',
              flexDirection: 'column',
              gap: 12
            }}
          >
            {/* Quick Switcher Strip */}
            <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap', paddingBottom: 6, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` }}>
              {plan.items.map(it => {
                const active = it.key === selectedKey;
                return (
                  <Button
                    key={it.key}
                    size="small"
                    appearance={active ? 'primary' : 'subtle'}
                    onClick={() => setSelectedKey(it.key)}
                  >
                    {it.key}
                  </Button>
                );
              })}
            </div>

            {!selectedItem ? (
              <Text size={300} style={{ color: tokens.colorNeutralForeground3 }}>
                Select a node on the canvas (or add a work item) to inspect and edit its details.
              </Text>
            ) : (
              <>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <Badge appearance="filled" color={selectedItem.isAcceptanceTest ? 'important' : 'brand'}>
                      {selectedItem.key}
                    </Badge>
                    <Text size={300} weight="semibold">
                      Work Item Inspector ({selectedIndex + 1} of {plan.items.length})
                    </Text>
                  </div>
                  <div style={{ display: 'flex', gap: 4 }}>
                    <Button
                      size="small"
                      appearance="subtle"
                      disabled={selectedIndex <= 0}
                      onClick={() => setSelectedKey(plan.items[selectedIndex - 1].key)}
                    >
                      ← Prev
                    </Button>
                    <Button
                      size="small"
                      appearance="subtle"
                      disabled={selectedIndex >= plan.items.length - 1}
                      onClick={() => setSelectedKey(plan.items[selectedIndex + 1].key)}
                    >
                      Next →
                    </Button>
                  </div>
                </div>

                {/* Full Multi-Line Title */}
                <div>
                  <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
                    Issue Title
                  </Text>
                  <Textarea
                    rows={2}
                    value={selectedItem.title}
                    onChange={(_, d) => updateSelectedItem({ title: d.value })}
                    style={{ width: '100%' }}
                  />
                </div>

                {/* Clearly Labeled Effort Scope & Scheduling Priority */}
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                  <div>
                    <Text size={200} weight="semibold" block>
                      Effort Scope (Issue Size)
                    </Text>
                    <Text size={100} block style={{ color: tokens.colorNeutralForeground3, marginBottom: 4 }}>
                      Expected coding complexity per PR
                    </Text>
                    <Dropdown
                      style={{ width: '100%', minWidth: 0 }}
                      value={EFFORT_LABELS[selectedItem.size]?.short ?? selectedItem.size}
                      selectedOptions={[selectedItem.size]}
                      onOptionSelect={(_, d) => d.optionValue && updateSelectedItem({ size: d.optionValue as ItemSize })}
                    >
                      {(Object.keys(EFFORT_LABELS) as ItemSize[]).map(sz => (
                        <Option key={sz} value={sz} text={EFFORT_LABELS[sz].short}>
                          {EFFORT_LABELS[sz].detail}
                        </Option>
                      ))}
                    </Dropdown>
                  </div>

                  <div>
                    <Text size={200} weight="semibold" block>
                      Scheduling Priority
                    </Text>
                    <Text size={100} block style={{ color: tokens.colorNeutralForeground3, marginBottom: 4 }}>
                      Order Copilot picks unblocked items
                    </Text>
                    <Dropdown
                      style={{ width: '100%', minWidth: 0 }}
                      value={PRIORITY_LABELS[selectedItem.priority]?.short ?? selectedItem.priority}
                      selectedOptions={[selectedItem.priority]}
                      onOptionSelect={(_, d) =>
                        d.optionValue && updateSelectedItem({ priority: d.optionValue as PriorityLevel })
                      }
                    >
                      {(Object.keys(PRIORITY_LABELS) as PriorityLevel[]).map(pr => (
                        <Option key={pr} value={pr} text={PRIORITY_LABELS[pr].short}>
                          {PRIORITY_LABELS[pr].detail}
                        </Option>
                      ))}
                    </Dropdown>
                  </div>
                </div>

                {/* Type & Initial Board Column */}
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                  <div>
                    <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
                      Work Item Role
                    </Text>
                    <Dropdown
                      style={{ width: '100%', minWidth: 0 }}
                      value={selectedItem.isAcceptanceTest ? 'Locked Acceptance Test' : 'Implementation Slice'}
                      selectedOptions={[selectedItem.isAcceptanceTest ? 'at' : 'impl']}
                      onOptionSelect={(_, d) =>
                        updateSelectedItem({ isAcceptanceTest: d.optionValue === 'at' })
                      }
                    >
                      <Option value="impl">Implementation Slice</Option>
                      <Option value="at">Locked Acceptance Test</Option>
                    </Dropdown>
                  </div>

                  <div>
                    <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
                      Initial Board Column
                    </Text>
                    <Dropdown
                      style={{ width: '100%', minWidth: 0 }}
                      value={selectedItem.initialStatus === 'Ready' ? 'Ready (Auto-queue)' : 'Backlog (Hold)'}
                      selectedOptions={[selectedItem.initialStatus]}
                      onOptionSelect={(_, d) =>
                        d.optionValue && updateSelectedItem({ initialStatus: d.optionValue as BoardStatus })
                      }
                    >
                      <Option value="Ready">Ready (Auto-queue)</Option>
                      <Option value="Backlog">Backlog (Hold)</Option>
                    </Dropdown>
                  </div>
                </div>

                {/* Description */}
                <div>
                  <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
                    Specification / Implementation Scope
                  </Text>
                  <Textarea
                    rows={4}
                    value={selectedItem.description}
                    onChange={(_, d) => updateSelectedItem({ description: d.value })}
                    style={{ width: '100%' }}
                  />
                </div>

                {/* Acceptance Criteria */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <Text size={200} weight="semibold">
                      Acceptance Criteria ({selectedItem.acceptanceCriteria.length})
                    </Text>
                    <Button
                      size="small"
                      appearance="subtle"
                      onClick={() =>
                        updateSelectedItem({
                          acceptanceCriteria: [...selectedItem.acceptanceCriteria, 'New testable acceptance criterion']
                        })
                      }
                    >
                      + Add Criterion
                    </Button>
                  </div>
                  {selectedItem.acceptanceCriteria.length === 0 && (
                    <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>
                      ⚠ Add at least one testable acceptance criterion so the review gate can verify this issue.
                    </Text>
                  )}
                  {selectedItem.acceptanceCriteria.map((criterion, cIdx) => (
                    <div key={cIdx} style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                      <Input
                        style={{ flex: 1 }}
                        value={criterion}
                        onChange={(_, d) => {
                          const next = selectedItem.acceptanceCriteria.map((c, i) => (i === cIdx ? d.value : c));
                          updateSelectedItem({ acceptanceCriteria: next });
                        }}
                      />
                      <Button
                        size="small"
                        appearance="subtle"
                        title="Remove criterion"
                        onClick={() =>
                          updateSelectedItem({
                            acceptanceCriteria: selectedItem.acceptanceCriteria.filter((_, i) => i !== cIdx)
                          })
                        }
                      >
                        ✕
                      </Button>
                    </div>
                  ))}
                </div>

                {/* Dependencies (Blocked By) */}
                <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                  <Text size={200} weight="semibold">
                    Dependencies — Must Merge First ({selectedItem.blockedByKeys.length})
                  </Text>
                  <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                    {selectedItem.blockedByKeys.length === 0 ? (
                      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                        Unblocked — eligible to start immediately once scheduled.
                      </Text>
                    ) : (
                      selectedItem.blockedByKeys.map(depKey => {
                        const depItem = plan.items.find(i => i.key === depKey);
                        return (
                          <span
                            key={depKey}
                            style={{
                              display: 'inline-flex',
                              alignItems: 'center',
                              gap: 6,
                              padding: '3px 8px',
                              borderRadius: 6,
                              fontSize: 12,
                              background: tokens.colorNeutralBackground1,
                              border: `1px solid ${tokens.colorNeutralStroke1}`
                            }}
                          >
                            <strong>{depKey}</strong>
                            {depItem ? `· ${depItem.title.slice(0, 28)}${depItem.title.length > 28 ? '…' : ''}` : ''}
                            <button
                              type="button"
                              onClick={() => toggleDependency(depKey, false)}
                              style={{
                                border: 'none',
                                background: 'transparent',
                                cursor: 'pointer',
                                color: tokens.colorNeutralForeground3,
                                padding: '0 2px',
                                fontWeight: 700
                              }}
                              title={`Remove dependency on ${depKey}`}
                            >
                              ✕
                            </button>
                          </span>
                        );
                      })
                    )}
                  </div>

                  {availableBlockers.length > 0 && (
                    <Dropdown
                      placeholder="+ Add prerequisite issue that must merge first…"
                      value=""
                      selectedOptions={[]}
                      onOptionSelect={(_, d) => d.optionValue && toggleDependency(d.optionValue, true)}
                    >
                      {availableBlockers.map(cand => (
                        <Option key={cand.key} value={cand.key} text={`${cand.key}: ${cand.title}`}>
                          {cand.key}: {cand.title}
                        </Option>
                      ))}
                    </Dropdown>
                  )}
                </div>

                {/* Delete Button */}
                <div
                  style={{
                    marginTop: 4,
                    paddingTop: 10,
                    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center'
                  }}
                >
                  <Text size={100} style={{ color: tokens.colorNeutralForeground3 }}>
                    Requirements: {selectedItem.requirementIds.join(', ') || 'None'}
                  </Text>
                  <Button size="small" appearance="subtle" onClick={deleteSelectedItem}>
                    Delete {selectedItem.key}
                  </Button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
};
