import React, { useMemo } from 'react';
import { Badge, Card, Text, tokens } from '@fluentui/react-components';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { Background, Controls, Edge, MarkerType, Node, ReactFlow } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { api } from '../../api/client';
import { EmptyState, PageSkeleton } from '../../components/SharedComponents';

export const GraphPage: React.FC<{ repoId?: string }> = ({ repoId }) => {
  const navigate = useNavigate();

  const graphQuery = useQuery({
    queryKey: ['graph', repoId],
    queryFn: () => api.getGraph(repoId!),
    enabled: Boolean(repoId)
  });

  const { nodes, edges } = useMemo(() => {
    const rawNodes: any[] = graphQuery.data?.nodes ?? [];
    const rawEdges: any[] = graphQuery.data?.edges ?? [];

    const rfNodes: Node[] = rawNodes.map((n, idx) => {
      const col = idx % 3;
      const row = Math.floor(idx / 3);
      const borderColor = n.inCycle
        ? '#dc2626'
        : n.onCriticalPath
        ? '#f59e0b'
        : n.boardStatus === 'Done'
        ? '#10b981'
        : '#2b82f0';

      return {
        id: n.id,
        position: { x: col * 300 + 40, y: row * 150 + 40 },
        className: n.isNextUp ? 'df-pulse-next' : undefined,
        data: {
          workItemId: n.workItemId,
          label: (
            <div style={{ textAlign: 'left', padding: 4 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, fontWeight: 700 }}>
                <span>#{n.issueNumber} · {n.boardStatus}</span>
                <span>{n.priority}</span>
              </div>
              <div style={{ fontWeight: 600, fontSize: 12, marginTop: 4 }}>{n.title}</div>
              {n.isNextUp && (
                <div style={{ color: '#2b82f0', fontSize: 11, fontWeight: 700, marginTop: 4 }}>● Next Up</div>
              )}
              {n.inCycle && (
                <div style={{ color: '#dc2626', fontSize: 11, marginTop: 4 }}>{n.cycleResolutionHint}</div>
              )}
            </div>
          )
        },
        style: {
          width: 255,
          borderRadius: 8,
          border: `2px solid ${borderColor}`,
          background: tokens.colorNeutralBackground1,
          color: tokens.colorNeutralForeground1
        }
      };
    });

    const rfEdges: Edge[] = rawEdges.map(e => ({
      id: e.id,
      source: e.source,
      target: e.target,
      animated: Boolean(e.onCriticalPath || e.inCycle),
      markerEnd: { type: MarkerType.ArrowClosed },
      style: {
        stroke: e.inCycle ? '#dc2626' : e.onCriticalPath ? '#f59e0b' : '#6b7280',
        strokeWidth: e.onCriticalPath || e.inCycle ? 2.5 : 1.5
      }
    }));

    return { nodes: rfNodes, edges: rfEdges };
  }, [graphQuery.data]);

  if (!repoId) {
    return <EmptyState title="No repository selected" description="Select a repository to inspect its dependency DAG." />;
  }
  if (graphQuery.isLoading) return <PageSkeleton cards={3} />;

  const cycleIssues: number[] = graphQuery.data?.cycleIssues ?? [];
  const criticalPath: number[] = graphQuery.data?.criticalPath ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16, height: 'calc(100vh - 150px)' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Dependency Graph (DAG & Tarjan Cycle Detection)
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Click any node to inspect the work item. Critical path is highlighted in amber; next-up items pulse.
          </Text>
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          <Badge appearance="tint" color="warning">
            Critical Path: {criticalPath.length > 0 ? criticalPath.map(n => `#${n}`).join(' → ') : 'None open'}
          </Badge>
          {cycleIssues.length > 0 && (
            <Badge appearance="filled" color="danger">
              Cycles Detected: {cycleIssues.map(n => `#${n}`).join(', ')}
            </Badge>
          )}
        </div>
      </div>

      <Card style={{ flex: 1, minHeight: 460, padding: 0, overflow: 'hidden' }}>
        <ReactFlow
          nodes={nodes}
          edges={edges}
          fitView
          onNodeClick={(_, node) => {
            if (node.data?.workItemId) navigate(`/workitems/${node.data.workItemId}`);
          }}
        >
          <Background />
          <Controls />
        </ReactFlow>
      </Card>
    </div>
  );
};
