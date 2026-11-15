import React from 'react';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Divider,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis
} from 'recharts';
import { api } from '../../api/client';
import { KpiCard, PageSkeleton, SessionTimeline, StatusPill } from '../../components/SharedComponents';

export const OverviewPage: React.FC<{ selectedRepoId?: string }> = () => {
  const navigate = useNavigate();
  const qc = useQueryClient();

  const metricsQuery = useQuery({ queryKey: ['metrics'], queryFn: () => api.getMetrics() });
  const approvalsQuery = useQuery({ queryKey: ['approvals'], queryFn: api.getApprovals });
  const reposQuery = useQuery({ queryKey: ['repos'], queryFn: api.getRepos });

  const approveMutation = useMutation({
    mutationFn: ({ workItemId }: { workItemId: string }) =>
      api.workItemAction(workItemId, 'approve', { reason: 'Approved from Overview inbox' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['approvals'] });
      qc.invalidateQueries({ queryKey: ['board'] });
      qc.invalidateQueries({ queryKey: ['metrics'] });
    }
  });

  const requestChangesMutation = useMutation({
    mutationFn: ({ workItemId }: { workItemId: string }) =>
      api.workItemAction(workItemId, 'request-changes', { reason: 'Operator requested changes from Overview' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['approvals'] });
      qc.invalidateQueries({ queryKey: ['board'] });
    }
  });

  const applyProposalMutation = useMutation({
    mutationFn: (proposalId: string) => api.applyChangeProposal(proposalId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['approvals'] });
      qc.invalidateQueries({ queryKey: ['board'] });
    }
  });

  if (metricsQuery.isLoading) return <PageSkeleton cards={8} />;
  const m = metricsQuery.data;
  const approvals = approvalsQuery.data?.workItemApprovals ?? [];
  const proposals = approvalsQuery.data?.changeProposals ?? [];
  const repos = reposQuery.data ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Factory Overview
          </Text>
          <Text size={300} style={{ color: tokens.colorNeutralForeground3 }}>
            Autonomous GitHub software factory health, quality gates, and operator approvals
          </Text>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <Button appearance="primary" onClick={() => navigate('/projects/new')}>
            New Project
          </Button>
          <Button appearance="secondary" onClick={() => navigate('/features')}>
            Add Feature
          </Button>
        </div>
      </div>

      {/* KPI Cards */}
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(210px, 1fr))',
          gap: 'var(--df-gap)'
        }}
      >
        <KpiCard
          label="Throughput"
          value={`${m.throughputPerDay}/day`}
          subtitle={`${m.totalMergedCount ?? 0} merged PR(s) over ${m.activeCalendarDays ?? 1} active day(s)`}
          accent="success"
        />
        <KpiCard
          label="Median Lead Time"
          value={m.medianLeadTimeMinutes != null ? `${m.medianLeadTimeMinutes}m` : '—'}
          subtitle={m.medianLeadTimeMinutes != null ? 'Ready → Merged' : 'No merged PRs yet'}
        />
        <KpiCard
          label="First-Pass Gate Rate"
          value={m.firstPassGateRatePercent != null ? `${m.firstPassGateRatePercent}%` : '—'}
          subtitle={m.firstPassGateRatePercent != null ? 'Green on Round 1' : 'No merged PRs yet'}
          accent="success"
        />
        <KpiCard
          label="Avg Review Rounds"
          value={m.averageReviewRounds != null ? m.averageReviewRounds : '—'}
          subtitle="Max 3 before Blocked"
        />
        <KpiCard
          label="Blocked Items"
          value={m.blockedCount}
          subtitle="Awaiting clarification or human"
          accent={m.blockedCount > 0 ? 'danger' : 'default'}
        />
        <KpiCard
          label="Sessions & Spend Today"
          value={`${m.sessionsToday}/${m.dailySessionBudget}`}
          subtitle={`$${Number(m.spendTodayUsd).toFixed(2)} / $${Number(m.dailySpendBudgetUsd).toFixed(2)} cap`}
        />
        <KpiCard
          label="Stop-the-Line MTTR"
          value={m.mttrMinutes != null ? `${m.mttrMinutes}m` : '—'}
          subtitle={`${m.lineStopIncidentCount ?? 0} incident(s) · ${m.flakyTestCount} flaky check(s)`}
        />
        <KpiCard
          label="Requirement Coverage"
          value={m.requirementCoveragePercent != null ? `${m.requirementCoveragePercent}%` : '—'}
          subtitle={
            m.requirementsTotal != null && m.requirementsTotal > 0
              ? `${m.requirementsPassing ?? 0} / ${m.requirementsTotal} requirements done`
              : 'No requirements tracked yet'
          }
          accent="success"
        />
      </div>

      {/* Approvals Inbox + Repo Health Tiles */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(360px, 1fr))', gap: 16 }}>
        <Card style={{ padding: 16 }}>
          <CardHeader
            header={
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Text size={400} weight="semibold">
                  Approvals Inbox
                </Text>
                <Badge appearance="filled" color={approvals.length + proposals.length > 0 ? 'warning' : 'success'}>
                  {approvals.length + proposals.length}
                </Badge>
              </div>
            }
          />
          <Divider style={{ margin: '8px 0 12px' }} />
          {approvals.length === 0 && proposals.length === 0 ? (
            <Text style={{ color: tokens.colorNeutralForeground3 }}>
              Nothing waiting on you — autonomous pipeline clear.
            </Text>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              {approvals.map((a: any) => (
                <div
                  key={a.id}
                  style={{
                    padding: 12,
                    borderRadius: 8,
                    background: tokens.colorNeutralBackground2,
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 8
                  }}
                >
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <Text
                      weight="semibold"
                      style={{ cursor: 'pointer', color: tokens.colorBrandForeground1 }}
                      onClick={() => navigate(`/workitems/${a.workItemId}`)}
                    >
                      #{a.issueNumber} {a.title}
                    </Text>
                    <Badge appearance="tint" color="warning">
                      {a.kind}
                    </Badge>
                  </div>
                  <Text size={200} style={{ color: tokens.colorNeutralForeground2 }}>
                    {a.summary}
                  </Text>
                  <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                    <Button
                      size="small"
                      appearance="primary"
                      disabled={approveMutation.isPending}
                      onClick={() => approveMutation.mutate({ workItemId: a.workItemId })}
                    >
                      Approve SHA {a.headSha?.slice(0, 7)}
                    </Button>
                    <Button
                      size="small"
                      appearance="secondary"
                      disabled={requestChangesMutation.isPending}
                      onClick={() => requestChangesMutation.mutate({ workItemId: a.workItemId })}
                    >
                      Request Changes
                    </Button>
                    {a.previewUrl && (
                      <Button size="small" appearance="subtle" as="a" href={a.previewUrl} target="_blank">
                        Open Preview
                      </Button>
                    )}
                  </div>
                </div>
              ))}
              {proposals.map((cp: any) => (
                <div
                  key={cp.id}
                  style={{
                    padding: 12,
                    borderRadius: 8,
                    background: tokens.colorNeutralBackground2,
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center',
                    gap: 8
                  }}
                >
                  <div>
                    <Text weight="semibold" block>
                      Spec Change Proposal (v{cp.fromSpecVersion} → v{cp.toSpecVersion})
                    </Text>
                    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                      {cp.summary}
                    </Text>
                  </div>
                  <Button
                    size="small"
                    appearance="primary"
                    onClick={() => applyProposalMutation.mutate(cp.id)}
                  >
                    Apply Proposal
                  </Button>
                </div>
              ))}
            </div>
          )}
        </Card>

        <Card style={{ padding: 16 }}>
          <CardHeader
            header={
              <Text size={400} weight="semibold">
                Repository Health Tiles
              </Text>
            }
          />
          <Divider style={{ margin: '8px 0 12px' }} />
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {repos.map(r => (
              <div
                key={r.id}
                style={{
                  padding: 12,
                  borderRadius: 8,
                  background: tokens.colorNeutralBackground2,
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  flexWrap: 'wrap',
                  gap: 8
                }}
              >
                <div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <Text weight="semibold">{r.fullName}</Text>
                    <StatusPill status={r.paused ? 'Paused' : r.lineStatus} />
                    {r.readinessPassed && (
                      <Badge appearance="outline" color="success">
                        Readiness ✓
                      </Badge>
                    )}
                  </div>
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                    In-flight WIP: {r.inFlightCount} · Ready queue: {r.readyQueueDepth} · Blocked: {r.blockedCount} · Inbox:{' '}
                    {r.newInboxCount} new
                  </Text>
                </div>
                <div style={{ display: 'flex', gap: 6 }}>
                  <Button size="small" onClick={() => navigate('/board')}>
                    Board
                  </Button>
                  <Button size="small" appearance="subtle" onClick={() => navigate('/inbox')}>
                    Inbox ({r.newInboxCount})
                  </Button>
                </div>
              </div>
            ))}
          </div>
        </Card>
      </div>

      {/* 14-day Real Trend Charts + Live Activity Feed */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(360px, 1fr))', gap: 16 }}>
        <Card style={{ padding: 16 }}>
          <div style={{ marginBottom: 12 }}>
            <Text size={400} weight="semibold" block>
              Throughput &amp; Review Rounds Trend (Last 14 Days)
            </Text>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              Live counts from merged pull requests, Copilot agent sessions, and review rounds (UTC days)
            </Text>
          </div>
          <div style={{ width: '100%', height: 220 }}>
            <ResponsiveContainer>
              <BarChart data={m.trend30Days}>
                <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
                <XAxis dataKey="date" tick={{ fontSize: 11 }} />
                <YAxis allowDecimals={false} tick={{ fontSize: 11 }} />
                <Tooltip />
                <Legend wrapperStyle={{ fontSize: 12 }} />
                <Bar dataKey="merged" name="Merged PRs" fill="#2b82f0" radius={[4, 4, 0, 0]} />
                <Bar dataKey="sessions" name="Copilot Sessions" fill="#8b5cf6" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
          <div style={{ width: '100%', height: 175, marginTop: 12 }}>
            <ResponsiveContainer>
              <LineChart data={m.trend30Days}>
                <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
                <XAxis dataKey="date" tick={{ fontSize: 11 }} />
                <YAxis yAxisId="rounds" allowDecimals={true} domain={[0, 'auto']} tick={{ fontSize: 11 }} />
                <YAxis yAxisId="lead" orientation="right" domain={[0, 'auto']} tick={{ fontSize: 11 }} unit="m" />
                <Tooltip />
                <Legend wrapperStyle={{ fontSize: 12 }} />
                <Line
                  yAxisId="rounds"
                  type="monotone"
                  dataKey="avgRounds"
                  name="Avg Review Rounds"
                  stroke="#10b981"
                  strokeWidth={2}
                  connectNulls
                />
                <Line
                  yAxisId="lead"
                  type="monotone"
                  dataKey="avgLeadMinutes"
                  name="Avg Lead Time (min)"
                  stroke="#f59e0b"
                  strokeWidth={2}
                  connectNulls
                />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </Card>

        <Card style={{ padding: 16 }}>
          <Text size={400} weight="semibold" block style={{ marginBottom: 12 }}>
            Live Activity Feed
          </Text>
          <SessionTimeline events={m.recentActivity ?? []} />
        </Card>
      </div>
    </div>
  );
};
