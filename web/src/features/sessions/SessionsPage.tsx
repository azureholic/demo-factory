import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dropdown,
  Option,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens
} from '@fluentui/react-components';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { PageSkeleton, StatusPill } from '../../components/SharedComponents';

export const SessionsPage: React.FC<{ repoId?: string }> = ({ repoId }) => {
  const navigate = useNavigate();
  const [mode, setMode] = useState<string>('');
  const [state, setState] = useState<string>('');
  const [model, setModel] = useState<string>('');

  const sessionsQuery = useQuery({
    queryKey: ['sessions', repoId, mode, state, model],
    queryFn: () =>
      api.getSessions({
        repoId,
        mode: mode || undefined,
        state: state || undefined,
        model: model || undefined
      })
  });

  if (sessionsQuery.isLoading) return <PageSkeleton cards={4} />;
  const rows = sessionsQuery.data ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Copilot Cloud Agent Sessions (§9.2, §14.2 Screen 5)
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Every delegated Copilot session with mode, resolved model, token usage, commits, and GitHub session log link
          </Text>
        </div>

        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Dropdown
            placeholder="All Modes"
            value={mode || 'All Modes'}
            onOptionSelect={(_, d) => setMode(d.optionValue === '__all__' ? '' : d.optionValue ?? '')}
          >
            <Option value="__all__">All Modes</Option>
            <Option value="Implement">Implement</Option>
            <Option value="Fix">Fix</Option>
            <Option value="ResolveConflict">ResolveConflict</Option>
          </Dropdown>
          <Dropdown
            placeholder="All States"
            value={state || 'All States'}
            onOptionSelect={(_, d) => setState(d.optionValue === '__all__' ? '' : d.optionValue ?? '')}
          >
            <Option value="__all__">All States</Option>
            <Option value="InProgress">InProgress</Option>
            <Option value="Completed">Completed</Option>
            <Option value="WaitingForUser">WaitingForUser</Option>
            <Option value="Failed">Failed</Option>
          </Dropdown>
          <Dropdown
            placeholder="All Models"
            value={model || 'All Models'}
            onOptionSelect={(_, d) => setModel(d.optionValue === '__all__' ? '' : d.optionValue ?? '')}
          >
            <Option value="__all__">All Models</Option>
            <Option value="gemini-4-carbon">gemini-4-carbon</Option>
            <Option value="claude-sonnet-5.5">claude-sonnet-5.5</Option>
            <Option value="gpt-5.3-codex">gpt-5.3-codex</Option>
          </Dropdown>
        </div>
      </div>

      <Card style={{ padding: 12, overflowX: 'auto' }}>
        <Table aria-label="Copilot sessions table">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Issue / PR</TableHeaderCell>
              <TableHeaderCell>Mode</TableHeaderCell>
              <TableHeaderCell>State</TableHeaderCell>
              <TableHeaderCell>Model</TableHeaderCell>
              <TableHeaderCell>Tokens / Cost</TableHeaderCell>
              <TableHeaderCell>Started</TableHeaderCell>
              <TableHeaderCell>Log</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((s: any) => (
              <TableRow key={s.id}>
                <TableCell>
                  <Text
                    weight="semibold"
                    style={{ cursor: 'pointer', color: tokens.colorBrandForeground1 }}
                    onClick={() => navigate(`/workitems/${s.workItemId}`)}
                  >
                    #{s.issueNumber ?? '—'} {s.issueTitle ?? s.copilotTaskId}
                  </Text>
                </TableCell>
                <TableCell>
                  <Badge appearance="outline">{s.mode}</Badge>
                </TableCell>
                <TableCell>
                  <StatusPill status={s.state} />
                </TableCell>
                <TableCell>{s.model}</TableCell>
                <TableCell>
                  {(s.tokensUsed ?? 0).toLocaleString()} · ${Number(s.estimatedCostUsd).toFixed(2)}
                </TableCell>
                <TableCell>{new Date(s.startedAt).toLocaleString()}</TableCell>
                <TableCell>
                  {s.sessionUrl && (
                    <Button size="small" appearance="subtle" as="a" href={s.sessionUrl} target="_blank">
                      GitHub Log ↗
                    </Button>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>
    </div>
  );
};
