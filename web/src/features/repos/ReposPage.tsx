import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dropdown,
  Input,
  Option,
  Spinner,
  Text,
  tokens
} from '@fluentui/react-components';
import { CheckmarkCircle20Filled, DismissCircle20Filled } from '@fluentui/react-icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { OperationProgressBanner, PageSkeleton } from '../../components/SharedComponents';

export const ReposPage: React.FC<{ selectedRepoId?: string }> = ({ selectedRepoId }) => {
  const qc = useQueryClient();
  const [owner, setOwner] = useState('azureholic');
  const [repoName, setRepoName] = useState('');
  const [autonomy, setAutonomy] = useState<'L0' | 'L1' | 'L2'>('L1');
  const [codingModel, setCodingModel] = useState('gemini-4-carbon');
  const [fixBanner, setFixBanner] = useState<string | null>(null);

  const readinessQuery = useQuery({
    queryKey: ['readiness', selectedRepoId],
    queryFn: () => api.getRepoReadiness(selectedRepoId!),
    enabled: Boolean(selectedRepoId)
  });

  const onboardMutation = useMutation({
    mutationFn: () => api.onboardRepo({ owner, name: repoName, autonomy, codingModel }),
    onSuccess: () => {
      setRepoName('');
      qc.invalidateQueries({ queryKey: ['repos'] });
      qc.invalidateQueries({ queryKey: ['projects'] });
    }
  });

  const fixMutation = useMutation({
    mutationFn: () => api.fixRepoReadiness(selectedRepoId!),
    onSuccess: res => {
      setFixBanner(`Applied baseline rulesets and opened PR #${res.onboardingPrNumber} with factory-coder profile & setup steps.`);
      qc.invalidateQueries({ queryKey: ['readiness', selectedRepoId] });
    }
  });

  if (readinessQuery.isLoading) return <PageSkeleton cards={2} />;
  const checks = readinessQuery.data?.checks ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <Text size={600} weight="bold">
        Repositories &amp; Onboarding Wizard
      </Text>

      {fixBanner && (
        <Card style={{ padding: 12, background: tokens.colorPaletteGreenBackground1 }}>
          <Text weight="semibold">{fixBanner}</Text>
        </Card>
      )}

      <OperationProgressBanner
        active={onboardMutation.isPending || fixMutation.isPending}
        title={
          onboardMutation.isPending
            ? `Inspecting ${owner}/${repoName} on GitHub & onboarding repository…`
            : 'Applying governance rulesets & opening onboarding profile PR on GitHub…'
        }
        subtitle="Evaluating branch protection rulesets, workflow token permissions, environments, and Copilot agent readiness."
      />

      <div style={{ display: 'grid', gridTemplateColumns: '1.3fr 1fr', gap: 16 }}>
        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <Text size={400} weight="semibold">
              Target Repository Readiness Checklist — {readinessQuery.data?.repoFullName ?? 'None'}
            </Text>
            {selectedRepoId && (
              <Button
                size="small"
                appearance="primary"
                icon={fixMutation.isPending ? <Spinner size="tiny" /> : undefined}
                disabled={fixMutation.isPending}
                onClick={() => fixMutation.mutate()}
              >
                {fixMutation.isPending ? 'Applying Rulesets…' : 'Apply Rulesets & Open Profile PR'}
              </Button>
            )}
          </div>

          {checks.map((c: any) => (
            <div
              key={c.key}
              style={{
                padding: '10px 12px',
                borderRadius: 6,
                background: tokens.colorNeutralBackground2,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                gap: 10
              }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                {c.passed ? (
                  <CheckmarkCircle20Filled style={{ color: tokens.colorPaletteGreenForeground1 }} />
                ) : (
                  <DismissCircle20Filled style={{ color: tokens.colorPaletteRedForeground1 }} />
                )}
                <div>
                  <Text weight="semibold" block>
                    {c.label}
                  </Text>
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                    {c.detail}
                  </Text>
                </div>
              </div>
              {c.canAutoFix && <Badge appearance="outline">Auto-remediable</Badge>}
            </div>
          ))}
        </Card>

        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Text size={400} weight="semibold">
            Onboard Existing GitHub Repository
          </Text>
          <Input placeholder="Owner (e.g. azureholic)" value={owner} onChange={(_, d) => setOwner(d.value)} />
          <Input placeholder="Repository name" value={repoName} onChange={(_, d) => setRepoName(d.value)} />
          <Dropdown value={autonomy} onOptionSelect={(_, d) => d.optionValue && setAutonomy(d.optionValue as any)}>
            <Option value="L0">L0 Shadow</Option>
            <Option value="L1">L1 Supervised</Option>
            <Option value="L2">L2 Dark</Option>
          </Dropdown>
          <Dropdown value={codingModel} onOptionSelect={(_, d) => d.optionValue && setCodingModel(d.optionValue)}>
            <Option value="gemini-4-carbon">gemini-4-carbon</Option>
            <Option value="claude-sonnet-5.5">claude-sonnet-5.5</Option>
            <Option value="gpt-5.3-codex">gpt-5.3-codex</Option>
          </Dropdown>
          <Button
            appearance="primary"
            icon={onboardMutation.isPending ? <Spinner size="tiny" /> : undefined}
            disabled={!owner.trim() || !repoName.trim() || onboardMutation.isPending}
            onClick={() => onboardMutation.mutate()}
          >
            {onboardMutation.isPending ? 'Running Readiness Check…' : 'Run Readiness Check & Onboard'}
          </Button>
        </Card>
      </div>
    </div>
  );
};
