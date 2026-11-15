import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Checkbox,
  Spinner,
  Text,
  tokens
} from '@fluentui/react-components';
import { Warning20Filled } from '@fluentui/react-icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { EmptyState, OperationProgressBanner, PageSkeleton, StatusPill } from '../../components/SharedComponents';

export const InboxPage: React.FC<{ projectId?: string }> = ({ projectId }) => {
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [selectedNumbers, setSelectedNumbers] = useState<number[]>([]);
  const [synthesisPhase, setSynthesisPhase] = useState<0 | 1 | 2 | 3>(0);

  const inboxQuery = useQuery({
    queryKey: ['inbox', projectId],
    queryFn: () => api.getInbox(projectId!),
    enabled: Boolean(projectId)
  });

  const dismissMutation = useMutation({
    mutationFn: (id: string) => api.dismissInboxItem(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['inbox', projectId] });
      qc.invalidateQueries({ queryKey: ['projects'] });
      qc.invalidateQueries({ queryKey: ['repos'] });
    }
  });

  const regroupMutation = useMutation({
    mutationFn: () => api.regroupInbox(projectId!),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['inbox', projectId] })
  });

  const createFeatureMutation = useMutation({
    mutationFn: async () => {
      setSynthesisPhase(1);
      const drafted = await api.createFeatureFromIssues(projectId!, selectedNumbers);
      // Refresh inbox & badge immediately now that selected issues are converted into a feature spec
      qc.invalidateQueries({ queryKey: ['inbox', projectId] });
      qc.invalidateQueries({ queryKey: ['projects'] });
      qc.invalidateQueries({ queryKey: ['repos'] });

      setSynthesisPhase(2);
      await api.clarifyFeature(drafted.id, {}, true);

      setSynthesisPhase(3);
      await api.approveFeature(drafted.id);
      return drafted;
    },
    onSettled: () => setSynthesisPhase(0),
    onSuccess: () => {
      setSelectedNumbers([]);
      qc.invalidateQueries({ queryKey: ['inbox', projectId] });
      qc.invalidateQueries({ queryKey: ['projects'] });
      qc.invalidateQueries({ queryKey: ['repos'] });
      qc.invalidateQueries({ queryKey: ['features', projectId] });
      qc.invalidateQueries({ queryKey: ['board'] });
      navigate('/features');
    }
  });

  if (!projectId) {
    return <EmptyState title="No project selected" description="Select a project to review its unlabelled issue inbox." />;
  }
  if (inboxQuery.isLoading) return <PageSkeleton cards={3} />;

  const items = inboxQuery.data ?? [];
  const anyOwnerTriedDirectLabel = items.some((i: any) => i.ownerAttemptedDirectWorkLabel);

  const liveSynthesisSteps =
    synthesisPhase === 0
      ? undefined
      : [
          {
            stepNumber: 1,
            name: 'Drafting sanitized feature specification from untrusted inbox issues',
            status: synthesisPhase > 1 ? 'Completed' : 'InProgress'
          },
          {
            stepNumber: 2,
            name: 'Generating incremental requirements & backlog work items',
            status: synthesisPhase > 2 ? 'Completed' : synthesisPhase === 2 ? 'InProgress' : 'Pending'
          },
          {
            stepNumber: 3,
            name: 'Opening specification PR, creating backlog issues, and notifying scheduler',
            status: synthesisPhase === 3 ? 'InProgress' : 'Pending'
          }
        ];

  const toggleSelect = (num: number, checked: boolean) => {
    setSelectedNumbers(prev => (checked ? [...prev, num] : prev.filter(x => x !== num)));
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Issue Inbox — Unmanaged &amp; Community Input
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Issues without a trusted work label are never assigned directly to Copilot. Once turned into a feature spec, issues leave this inbox automatically.
          </Text>
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {items.length >= 2 && (
            <Button
              appearance="secondary"
              icon={regroupMutation.isPending ? <Spinner size="tiny" /> : undefined}
              disabled={regroupMutation.isPending || createFeatureMutation.isPending}
              onClick={() => regroupMutation.mutate()}
            >
              {regroupMutation.isPending ? 'Clustering…' : 'Cluster & Find Duplicates (AI)'}
            </Button>
          )}
          <Button
            appearance="primary"
            icon={createFeatureMutation.isPending ? <Spinner size="tiny" /> : undefined}
            disabled={selectedNumbers.length === 0 || createFeatureMutation.isPending}
            onClick={() => createFeatureMutation.mutate()}
          >
            {createFeatureMutation.isPending
              ? `Synthesizing Feature (${selectedNumbers.length})…`
              : `Create Feature from Selected (${selectedNumbers.length})`}
          </Button>
        </div>
      </div>

      <OperationProgressBanner
        active={createFeatureMutation.isPending}
        title="Synthesizing vetted feature specification & backlog increment from selected issues…"
        subtitle="Microsoft Foundry orchestrator-model (MAI-Thinking-1) is drafting the feature spec, generating plan items, and opening the specification PR."
        liveSteps={liveSynthesisSteps}
      />

      {anyOwnerTriedDirectLabel && (
        <Card
          style={{
            padding: 12,
            background: tokens.colorPaletteYellowBackground1,
            borderLeft: `4px solid ${tokens.colorPaletteMarigoldBorderActive}`
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <Warning20Filled />
            <Text weight="semibold">
              Manual <code>factory</code> labels on GitHub issues are ignored and removed automatically: Demo-Factory only
              executes vetted tasks generated by the factory itself from an approved specification. Select the issue(s)
              below and click &ldquo;Create Feature from Selected&rdquo; instead.
            </Text>
          </div>
        </Card>
      )}

      {items.length === 0 ? (
        <EmptyState title="Issue inbox is empty" description="All open unlabelled issues have been triaged or dismissed." />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {items.map((item: any) => (
            <Card key={item.id} style={{ padding: 14, display: 'flex', flexDirection: 'column', gap: 8 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 8 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <Checkbox
                    checked={selectedNumbers.includes(item.issueNumber)}
                    onChange={(_, d) => toggleSelect(item.issueNumber, Boolean(d.checked))}
                  />
                  <Text weight="semibold">
                    #{item.issueNumber} {item.title}
                  </Text>
                  <StatusPill status={item.state} />
                  {item.suggestedGroup && (
                    <Badge appearance="tint" color="brand">
                      Group: {item.suggestedGroup}
                    </Badge>
                  )}
                  {item.duplicateOfIssueNumber && (
                    <Badge appearance="outline" color="warning">
                      Likely duplicate of #{item.duplicateOfIssueNumber}
                    </Badge>
                  )}
                </div>
                <div style={{ display: 'flex', gap: 6 }}>
                  {item.linkedFeature && (
                    <Badge appearance="filled" color="success">
                      Feature: {item.linkedFeature.slug} ({item.linkedFeature.state})
                    </Badge>
                  )}
                  <Button size="small" appearance="subtle" onClick={() => dismissMutation.mutate(item.id)}>
                    Dismiss
                  </Button>
                  <Button size="small" appearance="outline" as="a" href={item.htmlUrl} target="_blank">
                    Open in GitHub
                  </Button>
                </div>
              </div>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                Reported by @{item.author} · 👍 {item.reactionsCount} · {new Date(item.createdAt).toLocaleDateString()}
              </Text>
              <Text size={200}>{item.body}</Text>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
};
