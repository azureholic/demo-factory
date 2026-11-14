import React, { useState } from 'react';
import {
  Button,
  Card,
  Dropdown,
  Input,
  Option,
  Switch,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { DensityMode, ThemeMode } from '../../theme/factoryTheme';

export const SettingsPage: React.FC<{
  themeMode: ThemeMode;
  onThemeChange: (m: ThemeMode) => void;
  density: DensityMode;
  onDensityChange: (d: DensityMode) => void;
}> = ({ themeMode, onThemeChange, density, onDensityChange }) => {
  const settingsQuery = useQuery({ queryKey: ['settings'], queryFn: api.getSettings });
  const global = settingsQuery.data?.global ?? {};
  const [teamsUrl, setTeamsUrl] = useState<string>(global.teamsWebhookUrl ?? '');
  const [digest, setDigest] = useState<boolean>(global.dailyDigestEnabled ?? true);
  const [savedBanner, setSavedBanner] = useState(false);

  const saveNotifications = useMutation({
    mutationFn: () => api.saveGlobalSettings({ teamsWebhookUrl: teamsUrl, dailyDigestEnabled: digest }),
    onSuccess: () => setSavedBanner(true)
  });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16, maxWidth: 760 }}>
      <Text size={600} weight="bold">
        Operator Preferences &amp; Notifications
      </Text>

      <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 14 }}>
        <Text size={400} weight="semibold">
          Appearance & Density (Fluent 2 CSS Variables — Instant Switch)
        </Text>
        <div>
          <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
            Theme Mode
          </Text>
          <Dropdown
            value={themeMode}
            onOptionSelect={(_, d) => d.optionValue && onThemeChange(d.optionValue as ThemeMode)}
          >
            <Option value="system">System (follows prefers-color-scheme)</Option>
            <Option value="light">Light</Option>
            <Option value="dark">Dark</Option>
          </Dropdown>
        </div>

        <div>
          <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
            Layout Density
          </Text>
          <Dropdown
            value={density}
            onOptionSelect={(_, d) => d.optionValue && onDensityChange(d.optionValue as DensityMode)}
          >
            <Option value="comfortable">Comfortable</Option>
            <Option value="compact">Compact (data-heavy)</Option>
          </Dropdown>
        </div>
      </Card>

      <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
        <Text size={400} weight="semibold">
          Notifications (Microsoft Teams Webhook & Daily Digest)
        </Text>
        <Input
          placeholder="https://contoso.webhook.office.com/webhookb2/..."
          value={teamsUrl}
          onChange={(_, d) => setTeamsUrl(d.value)}
        />
        <Switch
          label="Send daily factory throughput & blocked-item digest"
          checked={digest}
          onChange={(_, d) => setDigest(d.checked)}
        />
        <Button appearance="primary" onClick={() => saveNotifications.mutate()} style={{ width: 'fit-content' }}>
          Save Notification Settings
        </Button>
        {savedBanner && (
          <Text size={200} style={{ color: tokens.colorPaletteGreenForeground1 }}>
            Notification settings saved.
          </Text>
        )}
      </Card>

      <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 6 }}>
        <Text size={400} weight="semibold">
          Keyboard Shortcuts
        </Text>
        <Text size={200}>
          <code>Ctrl + K</code> — Open global command & work-item search palette
        </Text>
      </Card>
    </div>
  );
};
