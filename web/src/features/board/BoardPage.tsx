import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dropdown,
  Option,
  Text,
  Tooltip,
  tokens
} from '@fluentui/react-components';
import { LockClosed16Regular } from '@fluentui/react-icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { BoardCardDto, BoardStatus, api } from '../../api/client';
import { EmptyState, PageSkeleton, SessionRing, StatusPill } from '../../components/SharedComponents';

const COLUMNS: { key: BoardStatus; label: string; agentOwned: boolean }[] = [
  { key: 'Backlog', label: 'Backlog', agentOwned: false },
  { key: 'Ready', label: 'Ready', agentOwned: false },
  { key: 'InProgress', label: 'In Progress', agentOwned: true },
  { key: 'InReview', label: 'In Review', agentOwned: true },
  { key: 'Blocked', label: 'Blocked', agentOwned: false },
  { key: 'Done', label: 'Done', agentOwned: true }
];

export const BoardPage: React.FC<{ repoId?: string; projectId?: string }> = ({ repoId, projectId }) => {
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [featureFilter, setFeatureFilter] = useState<string>('');
  const [draggedCard, setDraggedCard] = useState<BoardCardDto | null>(null);
  const [moveError, setMoveError] = useState<string | null>(null);

  const boardQuery = useQuery({
    queryKey: ['board', repoId, featureFilter],
    queryFn: () => api.getBoard(repoId!, featureFilter || undefined),
    enabled: Boolean(repoId)
  });

  const featuresQuery = useQuery({
    queryKey: ['features', projectId],
    queryFn: () => api.getFeatures(projectId!),
    enabled: Boolean(projectId)
  });

  const moveMutation = useMutation({
    mutationFn: ({ id, toStatus }: { id: string; toStatus: BoardStatus }) =>
      api.workItemAction(id, 'move-board', { toStatus }),
    onSuccess: () => {
      setMoveError(null);
      qc.invalidateQueries({ queryKey: ['board'] });
      qc.invalidateQueries({ queryKey: ['graph'] });
    },
    onError: (err: Error) => setMoveError(err.message)
  });

  const markReadyMutation = useMutation({
    mutationFn: (id: string) => api.workItemAction(id, 'mark-ready'),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['board'] });
      qc.invalidateQueries({ queryKey: ['graph'] });
    }
  });

  if (!repoId) {
    return <EmptyState title="No repository selected" description="Select or create a project to view its board." />;
  }
  if (boardQuery.isLoading) return <PageSkeleton cards={6} />;

  const cards = boardQuery.data?.cards ?? [];
  const features = featuresQuery.data ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Project Board — {boardQuery.data?.repoFullName}
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Mirrors GitHub Projects v2. Humans may drag between Backlog, Ready, and Blocked; agent columns are locked (§14.2).
          </Text>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <Dropdown
            placeholder="Filter by feature"
            value={featureFilter ? `feature:${featureFilter}` : 'All features'}
            onOptionSelect={(_, d) => setFeatureFilter(d.optionValue === '__all__' ? '' : d.optionValue ?? '')}
          >
            <Option value="__all__">All features</Option>
            {features.map((f: any) => (
              <Option key={f.slug} value={f.slug}>
                {`feature:${f.slug}`}
              </Option>
            ))}
          </Dropdown>
        </div>
      </div>

      {moveError && (
        <Card style={{ padding: 10, background: tokens.colorPaletteRedBackground1 }}>
          <Text weight="semibold">{moveError}</Text>
        </Card>
      )}

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(6, minmax(245px, 1fr))',
          gap: 12,
          overflowX: 'auto',
          paddingBottom: 12
        }}
      >
        {COLUMNS.map(col => {
          const colCards = cards.filter(c => c.boardStatus === col.key);
          return (
            <div
              key={col.key}
              onDragOver={e => {
                if (!col.agentOwned) e.preventDefault();
              }}
              onDrop={e => {
                e.preventDefault();
                if (col.agentOwned) {
                  setMoveError(`Column '${col.label}' is managed deterministically by the factory orchestrator.`);
                  return;
                }
                if (draggedCard && draggedCard.boardStatus !== col.key) {
                  moveMutation.mutate({ id: draggedCard.id, toStatus: col.key });
                }
                setDraggedCard(null);
              }}
              style={{
                background: tokens.colorNeutralBackground2,
                borderRadius: 10,
                padding: 10,
                minHeight: 480,
                display: 'flex',
                flexDirection: 'column',
                gap: 10
              }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '4px 6px' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <Text weight="semibold">{col.label}</Text>
                  {col.agentOwned && (
                    <Tooltip
                      content="Agent-owned column: transitions are driven deterministically by webhooks, CI, and the review gate."
                      relationship="label"
                    >
                      <span>
                        <LockClosed16Regular style={{ color: tokens.colorNeutralForeground3 }} />
                      </span>
                    </Tooltip>
                  )}
                </div>
                <Badge appearance="tint">{colCards.length}</Badge>
              </div>

              {colCards.map(card => {
                const canDrag = !col.agentOwned && card.passesWorkLabelRule;
                return (
                  <Card
                    key={card.id}
                    draggable={canDrag}
                    onDragStart={() => setDraggedCard(card)}
                    className="df-card-border"
                    style={{
                      padding: 12,
                      opacity: card.passesWorkLabelRule ? 1 : 0.55,
                      cursor: 'pointer',
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 8
                    }}
                    onClick={() => navigate(`/workitems/${card.id}`)}
                  >
                    {!card.passesWorkLabelRule && (
                      <Badge appearance="outline" color="subtle">
                        Not managed by the factory
                      </Badge>
                    )}

                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 6 }}>
                      <Text size={200} weight="semibold" style={{ color: tokens.colorNeutralForeground3 }}>
                        #{card.issueNumber} · {card.size}
                      </Text>
                      <Badge
                        appearance="tint"
                        color={
                          card.priority === 'Critical'
                            ? 'danger'
                            : card.priority === 'High'
                            ? 'warning'
                            : 'informative'
                        }
                      >
                        {card.priority}
                      </Badge>
                    </div>

                    <Text weight="semibold" size={300}>
                      {card.title}
                    </Text>

                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center' }}>
                      <StatusPill status={card.state} />
                      <Badge appearance="outline">{card.codingModel}</Badge>
                      {card.currentRound > 0 && (
                        <Badge appearance="tint" color="brand">
                          Round {card.currentRound}/{card.maxRounds}
                        </Badge>
                      )}
                      {card.featureSlug && <Badge appearance="outline">{card.featureSlug}</Badge>}
                    </div>

                    {(card.blockedByCount > 0 || card.unblocksCount > 0) && (
                      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                        {card.blockedByCount > 0 ? `Blocked by ${card.blockedByCount} ` : ''}
                        {card.unblocksCount > 0 ? `· Unblocks ${card.unblocksCount}` : ''}
                      </Text>
                    )}

                    <SessionRing active={card.hasLiveSession} state={card.liveSessionState} />

                    <div
                      style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 6 }}
                      onClick={e => e.stopPropagation()}
                    >
                      {card.prUrl ? (
                        <a
                          href={card.prUrl}
                          target="_blank"
                          rel="noreferrer"
                          style={{ fontSize: 12, color: tokens.colorBrandForeground1 }}
                        >
                          PR #{card.prNumber}
                        </a>
                      ) : (
                        <span />
                      )}
                      {(card.boardStatus === 'Backlog' || card.boardStatus === 'Blocked') && card.passesWorkLabelRule && (
                        <Button
                          size="small"
                          appearance="subtle"
                          onClick={() => markReadyMutation.mutate(card.id)}
                        >
                          Mark ready
                        </Button>
                      )}
                    </div>
                  </Card>
                );
              })}
            </div>
          );
        })}
      </div>
    </div>
  );
};
