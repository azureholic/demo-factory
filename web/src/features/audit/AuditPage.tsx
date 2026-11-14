import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Input,
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
import { api } from '../../api/client';
import { PageSkeleton } from '../../components/SharedComponents';

export const AuditPage: React.FC = () => {
  const [search, setSearch] = useState('');

  const auditQuery = useQuery({
    queryKey: ['audit', search],
    queryFn: () => api.getAudit(search || undefined)
  });

  if (auditQuery.isLoading) return <PageSkeleton cards={4} />;
  const rows = auditQuery.data ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Append-Only Audit Log (§7.2, §14.2 Screen 8)
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Full traceability across issues, sessions, review rounds, gate evaluations, and merges
          </Text>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <Input
            placeholder="Search action, actor, SHA..."
            value={search}
            onChange={(_, d) => setSearch(d.value)}
          />
          <Button
            appearance="primary"
            as="a"
            href={`/api/audit/export.csv${search ? `?search=${encodeURIComponent(search)}` : ''}`}
          >
            Export CSV
          </Button>
        </div>
      </div>

      <Card style={{ padding: 12, overflowX: 'auto' }}>
        <Table aria-label="Audit events table">
          <TableHeader>
            <TableRow>
              <TableHeaderCell>Timestamp</TableHeaderCell>
              <TableHeaderCell>Actor</TableHeaderCell>
              <TableHeaderCell>Action</TableHeaderCell>
              <TableHeaderCell>Entity</TableHeaderCell>
              <TableHeaderCell>Correlations</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((e: any) => (
              <TableRow key={e.id}>
                <TableCell>{new Date(e.timestamp).toLocaleString()}</TableCell>
                <TableCell>
                  <Badge appearance="outline">{e.actor}</Badge>
                </TableCell>
                <TableCell>
                  <Text weight="semibold">{e.action}</Text>
                </TableCell>
                <TableCell>
                  {e.entityType} ({String(e.entityId).slice(0, 18)})
                </TableCell>
                <TableCell>
                  {e.issueNumber ? `Issue #${e.issueNumber} ` : ''}
                  {e.prNumber ? `PR #${e.prNumber} ` : ''}
                  {e.sha ? `SHA ${e.sha.slice(0, 7)}` : ''}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>
    </div>
  );
};
