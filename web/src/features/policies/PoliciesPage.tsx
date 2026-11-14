import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dropdown,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  Switch,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { PageSkeleton } from '../../components/SharedComponents';

export const PoliciesPage: React.FC<{ repoId?: string }> = ({ repoId }) => {
  const qc = useQueryClient();
  const [newModelId, setNewModelId] = useState('');
  const [newModelDisplay, setNewModelDisplay] = useState('');
  const [modeError, setModeError] = useState<string | null>(null);

  const meQuery = useQuery({ queryKey: ['me'], queryFn: api.getMe });
  const readinessQuery = useQuery({ queryKey: ['systemReadiness'], queryFn: api.getSystemReadiness });
  const modelsQuery = useQuery({ queryKey: ['models'], queryFn: api.getModels });
  const settingsQuery = useQuery({ queryKey: ['settings'], queryFn: api.getSettings });
  const repoSettingsQuery = useQuery({
    queryKey: ['repoSettings', repoId],
    queryFn: () => api.getRepoSettings(repoId!),
    enabled: Boolean(repoId)
  });
  const policyQuery = useQuery({
    queryKey: ['repoPolicy', repoId],
    queryFn: () => api.getRepoPolicy(repoId!),
    enabled: Boolean(repoId)
  });

  const saveGlobalMutation = useMutation({
    mutationFn: (payload: any) => api.saveGlobalSettings(payload),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['settings'] })
  });

  const saveRepoMutation = useMutation({
    mutationFn: (payload: any) => api.saveRepoSettings(repoId!, payload),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['repoSettings', repoId] });
      qc.invalidateQueries({ queryKey: ['repos'] });
    }
  });

  const upsertModelMutation = useMutation({
    mutationFn: ({ id, payload }: { id: string; payload: any }) => api.saveModel(id, payload),
    onSuccess: () => {
      setNewModelId('');
      setNewModelDisplay('');
      qc.invalidateQueries({ queryKey: ['models'] });
    }
  });

  const revokeMutation = useMutation({
    mutationFn: api.revokeGitHub,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['me'] });
      qc.invalidateQueries({ queryKey: ['factoryStatus'] });
      qc.invalidateQueries({ queryKey: ['systemReadiness'] });
    }
  });

  const toggleModeMutation = useMutation({
    mutationFn: (sandboxMode: boolean) => api.setExecutionMode(sandboxMode),
    onSuccess: () => {
      setModeError(null);
      qc.invalidateQueries({ queryKey: ['systemReadiness'] });
      qc.invalidateQueries({ queryKey: ['factoryStatus'] });
    },
    onError: (err: any) => {
      setModeError(err instanceof Error ? err.message : String(err));
    }
  });

  if (modelsQuery.isLoading || settingsQuery.isLoading) return <PageSkeleton cards={4} />;

  const models = modelsQuery.data ?? [];
  const globalSettings = settingsQuery.data?.global ?? {};
  const repoSettings = repoSettingsQuery.data ?? {};
  const policyData = policyQuery.data;
  const me = meQuery.data;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <Text size={600} weight="bold">
        Policies, Models &amp; Delegated GitHub Identity
      </Text>

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
        {/* Global & Per-Repo Runtime Settings */}
        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Text size={400} weight="semibold">
            Runtime Settings (Immediate — Audited in Azure SQL)
          </Text>

          <div>
            <Text size={200} weight="semibold" block>
              Global Default Coding Model (Copilot Cloud Agent)
            </Text>
            <Dropdown
              value={globalSettings.codingModel ?? 'gemini-4-carbon'}
              onOptionSelect={(_, d) => d.optionValue && saveGlobalMutation.mutate({ codingModel: d.optionValue })}
            >
              {models
                .filter((m: any) => m.enabled)
                .map((m: any) => (
                  <Option key={m.id} value={m.apiModelId}>
                    {`${m.displayName} (${m.apiModelId})`}
                  </Option>
                ))}
            </Dropdown>
          </div>

          <div>
            <Text size={200} weight="semibold" block>
              Orchestrator / Project Manager Model Deployment (Microsoft Foundry — MAI-Thinking)
            </Text>
            <Input
              defaultValue={globalSettings.pmModelDeployment ?? 'orchestrator-model'}
              onBlur={e => saveGlobalMutation.mutate({ pmModelDeployment: e.target.value })}
            />
          </div>

          {repoId && (
            <div
              style={{
                padding: 12,
                borderRadius: 8,
                background: tokens.colorNeutralBackground2,
                display: 'flex',
                flexDirection: 'column',
                gap: 10
              }}
            >
              <Text weight="semibold">Per-Repository Runtime Overrides</Text>
              <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
                <div>
                  <Text size={200} block>
                    Autonomy
                  </Text>
                  <Dropdown
                    value={repoSettings.autonomy ?? 'L1'}
                    onOptionSelect={(_, d) => d.optionValue && saveRepoMutation.mutate({ autonomy: d.optionValue })}
                  >
                    <Option value="L0">L0 Shadow</Option>
                    <Option value="L1">L1 Supervised</Option>
                    <Option value="L2">L2 Dark</Option>
                  </Dropdown>
                </div>
                <div>
                  <Text size={200} block>
                    WIP Slots
                  </Text>
                  <Input
                    type="number"
                    style={{ width: 85 }}
                    defaultValue={String(repoSettings.wip ?? 1)}
                    onBlur={e => saveRepoMutation.mutate({ wip: Number(e.target.value) || 1 })}
                  />
                </div>
                <div>
                  <Text size={200} block>
                    Max Rounds
                  </Text>
                  <Input
                    type="number"
                    style={{ width: 85 }}
                    defaultValue={String(repoSettings.maxRounds ?? 3)}
                    onBlur={e => saveRepoMutation.mutate({ maxRounds: Number(e.target.value) || 3 })}
                  />
                </div>
              </div>
            </div>
          )}
        </Card>

        {/* GitHub Connection & TokenBroker Status */}
        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Text size={400} weight="semibold">
            GitHub Connection &amp; TokenBroker
          </Text>
          <Text size={200}>
            Signed-in Account: <strong>@{me?.login}</strong> ({me?.role}) · Plan: <strong>{me?.copilotPlan}</strong>
          </Text>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <Text size={200}>Token:</Text>
            <Badge appearance="filled" color={me?.connection.connected ? 'success' : 'danger'}>
              {me?.connection.connected
                ? `Active (${readinessQuery.data?.gitHub.tokenKind ?? 'GitHub'})`
                : 'Reconnect Required'}
            </Badge>
            <Text size={200}>App Installation:</Text>
            <Badge
              appearance="tint"
              color={readinessQuery.data?.gitHub.appInstallation.installedOnOwner ? 'success' : 'warning'}
            >
              {readinessQuery.data?.gitHub.appInstallation.installedOnOwner
                ? `Installed (#${readinessQuery.data.gitHub.appInstallation.installationId})`
                : 'Not Installed on Account'}
            </Badge>
            <Text size={200}>PM Agent:</Text>
            <Badge appearance="tint" color={readinessQuery.data?.foundry.configured ? 'brand' : 'informative'}>
              {readinessQuery.data?.foundry.configured ? 'Live Foundry' : 'Local Fallback'}
            </Badge>
          </div>
          {readinessQuery.data?.gitHub.appInstallation.detail && (
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              {readinessQuery.data.gitHub.appInstallation.detail}
            </Text>
          )}
          {me?.connection.refreshTokenExpiresAt && (
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              Refresh token expires: {new Date(me.connection.refreshTokenExpiresAt).toLocaleDateString()} (encrypted locally / Key Vault)
            </Text>
          )}

          {me?.role === 'Admin' && readinessQuery.data && (
            <div
              style={{
                padding: 10,
                borderRadius: 6,
                background: tokens.colorNeutralBackground2,
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                gap: 12
              }}
            >
              <div>
                <Text weight="semibold" block>
                  Live GitHub API Execution
                </Text>
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                  {readinessQuery.data.sandboxMode
                    ? 'Sandbox Mode active — repositories, issues, and PRs are simulated locally.'
                    : 'Live GitHub Mode active — creates real repositories, rulesets, issues, and Copilot sessions on GitHub.'}
                </Text>
              </div>
              <Switch
                checked={!readinessQuery.data.sandboxMode}
                disabled={toggleModeMutation.isPending}
                onChange={(_, d) => toggleModeMutation.mutate(!d.checked)}
              />
            </div>
          )}

          {modeError && (
            <MessageBar intent="error">
              <MessageBarBody>{modeError}</MessageBarBody>
            </MessageBar>
          )}

          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            {readinessQuery.data?.gitHub.appInstallation &&
              !readinessQuery.data.gitHub.appInstallation.installedOnOwner && (
                <Button
                  appearance="primary"
                  as="a"
                  href={readinessQuery.data.gitHub.appInstallation.installUrl}
                  target="_blank"
                  rel="noreferrer"
                >
                  Install App on GitHub
                </Button>
              )}
            <Button appearance="secondary" as="a" href="/auth/github/login">
              Reconnect GitHub
            </Button>
            <Button appearance="outline" onClick={() => readinessQuery.refetch()}>
              Recheck Readiness
            </Button>
            <Button appearance="subtle" onClick={() => revokeMutation.mutate()}>
              Revoke Token &amp; Pause Factory
            </Button>
          </div>
        </Card>
      </div>

      {/* Model Catalogue + Structural YAML Policy */}
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
          <Text size={400} weight="semibold">
            Curated Coding Model Catalogue (Admin)
          </Text>
          {models.map((m: any) => (
            <div
              key={m.id}
              style={{
                padding: 10,
                borderRadius: 6,
                background: tokens.colorNeutralBackground2,
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center'
              }}
            >
              <div>
                <Text weight="semibold">{m.displayName}</Text>{' '}
                <Badge appearance="outline">{m.apiModelId}</Badge>{' '}
                <Badge appearance="tint" color={m.smokeTestPassed ? 'success' : 'danger'}>
                  {m.smokeTestPassed ? 'Smoke ✓' : 'Smoke ✗'}
                </Badge>
                {m.notes && (
                  <Text size={200} style={{ display: 'block', color: tokens.colorNeutralForeground3 }}>
                    {m.notes}
                  </Text>
                )}
              </div>
              <Switch
                checked={m.enabled}
                onChange={(_, d) =>
                  upsertModelMutation.mutate({
                    id: m.id,
                    payload: { displayName: m.displayName, apiModelId: m.apiModelId, enabled: d.checked, notes: m.notes }
                  })
                }
              />
            </div>
          ))}
          <div style={{ display: 'flex', gap: 8 }}>
            <Input
              placeholder="Model ID (e.g. gpt-5.6-sol)"
              value={newModelId}
              onChange={(_, d) => setNewModelId(d.value)}
            />
            <Input
              placeholder="Display Name"
              value={newModelDisplay}
              onChange={(_, d) => setNewModelDisplay(d.value)}
            />
            <Button
              appearance="secondary"
              disabled={!newModelId.trim() || !newModelDisplay.trim()}
              onClick={() =>
                upsertModelMutation.mutate({
                  id: newModelId.trim(),
                  payload: { displayName: newModelDisplay.trim(), apiModelId: newModelId.trim(), enabled: true }
                })
              }
            >
              Smoke-Test & Add
            </Button>
          </div>
        </Card>

        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <Text size={400} weight="semibold">
              Structural GitOps Policy ({policyData?.policyReference ?? 'policies/repos/*.yml'})
            </Text>
            <Badge appearance="tint" color={policyData?.isValid !== false ? 'success' : 'danger'}>
              {policyData?.isValid !== false ? 'Valid YAML' : 'Invalid'}
            </Badge>
          </div>
          <pre
            style={{
              margin: 0,
              padding: 12,
              borderRadius: 6,
              background: tokens.colorNeutralBackground2,
              fontSize: 12,
              overflowX: 'auto',
              maxHeight: 320
            }}
          >
            {policyData?.rawYaml ?? '# Select a repository to view its policy YAML'}
          </pre>
        </Card>
      </div>
    </div>
  );
};
