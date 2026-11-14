import React from 'react';
import {
  Badge,
  Button,
  Card,
  Skeleton,
  SkeletonItem,
  Text,
  tokens
} from '@fluentui/react-components';
import {
  CheckmarkCircle20Filled,
  Clock20Regular,
  DismissCircle20Filled,
  ErrorCircle20Filled,
  Warning20Filled
} from '@fluentui/react-icons';
import { GateConditionDto, RepoSummary, api } from '../api/client';
import { useMutation, useQueryClient } from '@tanstack/react-query';

export const StatusPill: React.FC<{ status: string }> = ({ status }) => {
  const color =
    status === 'Running' || status === 'Merged' || status === 'Done' || status === 'Pass' || status === 'Applied'
      ? 'success'
      : status === 'Blocked' || status === 'Failed' || status === 'LineStopped' || status === 'Paused'
      ? 'danger'
      : status === 'AwaitingApproval' || status === 'Degraded' || status === 'NeedsClarification'
      ? 'warning'
      : status === 'Implementing' || status === 'Validating' || status === 'Fixing' || status === 'InProgress'
      ? 'brand'
      : 'informative';

  return (
    <Badge appearance="tint" color={color} shape="rounded">
      {status}
    </Badge>
  );
};

export const SessionRing: React.FC<{ active: boolean; state?: string }> = ({ active, state }) => {
  if (!active) return null;
  return (
    <span
      title={`Copilot session active (${state ?? 'InProgress'})`}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 6,
        fontSize: 12,
        color: tokens.colorBrandForeground1,
        fontWeight: 600
      }}
    >
      <span
        className="df-pulse-next"
        style={{
          width: 10,
          height: 10,
          borderRadius: '50%',
          backgroundColor: tokens.colorBrandBackground,
          display: 'inline-block'
        }}
      />
      Copilot live
    </span>
  );
};

export const SeverityBadge: React.FC<{ severity: 'High' | 'Medium' | 'Low' | string }> = ({ severity }) => {
  const color = severity === 'High' ? 'danger' : severity === 'Medium' ? 'warning' : 'informative';
  return (
    <Badge appearance="filled" color={color}>
      {severity}
    </Badge>
  );
};

export const KpiCard: React.FC<{
  label: string;
  value: string | number;
  subtitle?: string;
  accent?: 'default' | 'danger' | 'success';
}> = ({ label, value, subtitle, accent = 'default' }) => (
  <Card
    className="df-card-border"
    style={{
      padding: 'var(--df-card-padding)',
      borderTop:
        accent === 'danger'
          ? `3px solid ${tokens.colorPaletteRedBorderActive}`
          : accent === 'success'
          ? `3px solid ${tokens.colorPaletteGreenBorderActive}`
          : `3px solid ${tokens.colorBrandStroke1}`
    }}
  >
    <Text size={200} style={{ color: tokens.colorNeutralForeground3, fontWeight: 600 }}>
      {label}
    </Text>
    <Text size={700} weight="bold" style={{ marginTop: 4 }}>
      {value}
    </Text>
    {subtitle && (
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
        {subtitle}
      </Text>
    )}
  </Card>
);

export const GateChecklist: React.FC<{ conditions: GateConditionDto[] }> = ({ conditions }) => {
  if (!conditions || conditions.length === 0) {
    return (
      <Text style={{ color: tokens.colorNeutralForeground3 }}>
        No gate evaluation recorded yet for the current head SHA.
      </Text>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {conditions.map(c => (
        <div
          key={c.number}
          style={{
            display: 'flex',
            alignItems: 'flex-start',
            gap: 10,
            padding: '8px 12px',
            borderRadius: 6,
            background: tokens.colorNeutralBackground2,
            opacity: c.applicable ? 1 : 0.65
          }}
        >
          <div style={{ marginTop: 2 }}>
            {!c.applicable ? (
              <Clock20Regular style={{ color: tokens.colorNeutralForeground3 }} />
            ) : c.passed ? (
              <CheckmarkCircle20Filled style={{ color: tokens.colorPaletteGreenForeground1 }} />
            ) : c.isPending ? (
              <Warning20Filled style={{ color: tokens.colorPaletteMarigoldForeground1 }} />
            ) : (
              <DismissCircle20Filled style={{ color: tokens.colorPaletteRedForeground1 }} />
            )}
          </div>
          <div style={{ flex: 1 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Text weight="semibold">
                #{c.number} {c.name}
              </Text>
              {!c.applicable && <Badge appearance="outline">Exempt</Badge>}
            </div>
            <Text size={200} style={{ color: tokens.colorNeutralForeground2, display: 'block' }}>
              {c.summary}
            </Text>
          </div>
        </div>
      ))}
    </div>
  );
};

export const SessionTimeline: React.FC<{ events: any[] }> = ({ events }) => {
  if (!events || events.length === 0) {
    return <Text style={{ color: tokens.colorNeutralForeground3 }}>No timeline events yet.</Text>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {events.map(ev => (
        <div
          key={ev.id}
          style={{
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            padding: '8px 12px',
            borderLeft: `3px solid ${tokens.colorBrandStroke1}`,
            background: tokens.colorNeutralBackground2,
            borderRadius: '0 6px 6px 0'
          }}
        >
          <div>
            <Text weight="semibold">{ev.action}</Text>
            <Text size={200} style={{ display: 'block', color: tokens.colorNeutralForeground3 }}>
              Actor: {ev.actor} · {ev.entityType} {ev.sha ? `· SHA ${ev.sha.slice(0, 7)}` : ''}
            </Text>
          </div>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            {new Date(ev.timestamp).toLocaleString()}
          </Text>
        </div>
      ))}
    </div>
  );
};

export const AndonBanner: React.FC<{ stoppedRepos: RepoSummary[]; reconnectRequired: boolean }> = ({
  stoppedRepos,
  reconnectRequired
}) => {
  const qc = useQueryClient();
  const resumeMutation = useMutation({
    mutationFn: (repoId: string) => api.resumeRepoLine(repoId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['repos'] });
      qc.invalidateQueries({ queryKey: ['factoryStatus'] });
    }
  });

  if (!reconnectRequired && stoppedRepos.length === 0) return null;

  return (
    <div
      role="alert"
      style={{
        background: tokens.colorPaletteRedBackground1,
        borderBottom: `2px solid ${tokens.colorPaletteRedBorderActive}`,
        padding: '10px 20px',
        display: 'flex',
        flexDirection: 'column',
        gap: 8
      }}
    >
      {reconnectRequired && (
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <ErrorCircle20Filled style={{ color: tokens.colorPaletteRedForeground1 }} />
            <Text weight="semibold">
              Reconnect GitHub: Your delegated user token expired or was revoked. All projects are paused.
            </Text>
          </div>
          <Button appearance="primary" as="a" href="/auth/github/login">
            Reconnect GitHub
          </Button>
        </div>
      )}
      {stoppedRepos.map(r => (
        <div key={r.id} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <ErrorCircle20Filled style={{ color: tokens.colorPaletteRedForeground1 }} />
            <Text weight="semibold">
              STOP THE LINE — {r.fullName}: {r.lineCause ?? 'Main workflow failed'}. Only revert and line-fix PRs may merge.
            </Text>
          </div>
          <Button
            size="small"
            appearance="secondary"
            disabled={resumeMutation.isPending}
            onClick={() => resumeMutation.mutate(r.id)}
          >
            Resume Line (Admin)
          </Button>
        </div>
      ))}
    </div>
  );
};

export const PageSkeleton: React.FC<{ cards?: number }> = ({ cards = 4 }) => (
  <Skeleton aria-label="Loading content">
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 16 }}>
      {Array.from({ length: cards }).map((_, idx) => (
        <Card key={idx} style={{ padding: 16 }}>
          <SkeletonItem size={16} style={{ width: '55%' }} />
          <SkeletonItem size={32} style={{ width: '40%', marginTop: 12 }} />
          <SkeletonItem size={12} style={{ width: '75%', marginTop: 12 }} />
        </Card>
      ))}
    </div>
  </Skeleton>
);

export const EmptyState: React.FC<{
  title: string;
  description: string;
  actionLabel?: string;
  onAction?: () => void;
}> = ({ title, description, actionLabel, onAction }) => (
  <Card style={{ padding: 32, textAlign: 'center', alignItems: 'center', gap: 10 }}>
    <Text size={500} weight="semibold">
      {title}
    </Text>
    <Text style={{ color: tokens.colorNeutralForeground3, maxWidth: 480 }}>{description}</Text>
    {actionLabel && onAction && (
      <Button appearance="primary" onClick={onAction} style={{ marginTop: 8 }}>
        {actionLabel}
      </Button>
    )}
  </Card>
);
