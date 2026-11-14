import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Checkbox,
  Text,
  tokens
} from '@fluentui/react-components';
import { Warning20Filled } from '@fluentui/react-icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { EmptyState, PageSkeleton, StatusPill } from '../../components/SharedComponents';

export const InboxPage: React.FC<{ projectId?: string }> = ({ projectId }) => {
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [selectedNumbers, setSelectedNumbers] = useState<number[]>([]);

  const inboxQuery = useQuery({
    queryKey: ['inbox', projectId],
    queryFn: () => api.getInbox(projectId!),
    enabled: Boolean(projectId)
  });

  const dismissMutation = useMutation({
    mutationFn: (id: string) => api.dismissInboxItem(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['inbox', projectId] })
  });

  const createFeatureMutation = useMutation({
    mutationFn: async () => {
      const drafted = await api.createFeatureFromIssues(projectId!, selectedNumbers);
      await api.clarifyFeature(drafted.id, {}, true);
      await api.approveFeature(drafted.id);
      return drafted;
    },
    onSuccess: () => {
      setSelectedNumbers([]);
      qc.invalidateQueries({ queryKey: ['inbox', projectId] });
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
            Issues without a trusted work label are never assigned directly to Copilot. Select items to synthesize a vetted feature spec.
          </Text>
        </div>
        <Button
          appearance="primary"
          disabled={selectedNumbers.length === 0 || createFeatureMutation.isPending}
          onClick={() => createFeatureMutation.mutate()}
        >
          Create Feature from Selected ({selectedNumbers.length})
        </Button>
      </div>

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
              Security boundary: Issues opened by outside users cannot be built directly even if you add the work
              label, because the original author could edit the text afterwards. Use &ldquo;Create Feature from Selected&rdquo; instead.
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
