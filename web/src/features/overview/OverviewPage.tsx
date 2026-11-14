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

  const metricsQuery = useQuery({ queryKey: ['metrics'], queryFn: api.getMetrics });
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
        <KpiCard label="Throughput" value={`${m.throughputPerDay}/day`} subtitle="Merged PRs per day" accent="success" />
        <KpiCard label="Median Lead Time" value={`${m.medianLeadTimeMinutes}m`} subtitle="Ready → Done" />
        <KpiCard label="First-Pass Gate Rate" value={`${m.firstPassGateRatePercent}%`} subtitle="Green on Round 1" accent="success" />
        <KpiCard label="Avg Review Rounds" value={m.averageReviewRounds} subtitle="Max 3 before Blocked" />
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
          value={`${m.mttrMinutes}m`}
          subtitle={`${m.flakyTestCount} flaky check(s) recorded`}
        />
        <KpiCard
          label="Requirement Coverage"
          value={`${m.requirementCoveragePercent}%`}
          subtitle="Requirements with passing ATs"
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

      {/* 30-day Trend Charts + Live Activity Feed */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(360px, 1fr))', gap: 16 }}>
        <Card style={{ padding: 16 }}>
          <Text size={400} weight="semibold" block style={{ marginBottom: 12 }}>
            Throughput & Review Rounds Trend
          </Text>
          <div style={{ width: '100%', height: 220 }}>
            <ResponsiveContainer>
              <BarChart data={m.trend30Days}>
                <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
                <XAxis dataKey="date" tick={{ fontSize: 11 }} />
                <YAxis tick={{ fontSize: 11 }} />
                <Tooltip />
                <Bar dataKey="merged" name="Merged PRs" fill="#2b82f0" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
          <div style={{ width: '100%', height: 160, marginTop: 12 }}>
            <ResponsiveContainer>
              <LineChart data={m.trend30Days}>
                <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
                <XAxis dataKey="date" tick={{ fontSize: 11 }} />
                <YAxis tick={{ fontSize: 11 }} />
                <Tooltip />
                <Line type="monotone" dataKey="costUsd" name="Avg Cost ($)" stroke="#4b95f4" strokeWidth={2} />
                <Line type="monotone" dataKey="avgRounds" name="Avg Rounds" stroke="#10b981" strokeWidth={2} />
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
