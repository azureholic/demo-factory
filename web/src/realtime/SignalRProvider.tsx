import React, { createContext, useContext, useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';

interface RealtimeContextValue {
  connected: boolean;
  lastAnnouncement: string;
}

const RealtimeContext = createContext<RealtimeContextValue>({
  connected: false,
  lastAnnouncement: ''
});

export const useRealtime = () => useContext(RealtimeContext);

export const SignalRProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const queryClient = useQueryClient();
  const [connected, setConnected] = useState(false);
  const [lastAnnouncement, setLastAnnouncement] = useState('');

  useEffect(() => {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/factory')
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    conn.on('workItemChanged', (ev: any) => {
      setLastAnnouncement(`Work item updated to ${ev.state}`);
      queryClient.invalidateQueries({ queryKey: ['board'] });
      queryClient.invalidateQueries({ queryKey: ['graph'] });
      queryClient.invalidateQueries({ queryKey: ['workitem', ev.workItemId] });
      queryClient.invalidateQueries({ queryKey: ['metrics'] });
      queryClient.invalidateQueries({ queryKey: ['repos'] });
    });

    conn.on('sessionEvent', (ev: any) => {
      setLastAnnouncement(`Copilot session ${ev.state}`);
      queryClient.invalidateQueries({ queryKey: ['sessions'] });
      queryClient.invalidateQueries({ queryKey: ['board'] });
      queryClient.invalidateQueries({ queryKey: ['workitem', ev.workItemId] });
    });

    conn.on('gateEvaluated', (ev: any) => {
      setLastAnnouncement(`Review gate evaluated: ${ev.outcome}`);
      queryClient.invalidateQueries({ queryKey: ['workitem', ev.workItemId] });
      queryClient.invalidateQueries({ queryKey: ['approvals'] });
      queryClient.invalidateQueries({ queryKey: ['metrics'] });
    });

    conn.on('approvalRequested', (ev: any) => {
      setLastAnnouncement(`Operator approval requested (${ev.kind})`);
      queryClient.invalidateQueries({ queryKey: ['approvals'] });
    });

    conn.on('lineStateChanged', (ev: any) => {
      setLastAnnouncement(`Line status changed to ${ev.status}`);
      queryClient.invalidateQueries({ queryKey: ['repos'] });
      queryClient.invalidateQueries({ queryKey: ['line'] });
      queryClient.invalidateQueries({ queryKey: ['factoryStatus'] });
    });

    conn.on('factoryStatus', (ev: any) => {
      setLastAnnouncement(ev.paused ? `Factory paused: ${ev.reason ?? ''}` : 'Factory resumed');
      queryClient.invalidateQueries({ queryKey: ['factoryStatus'] });
      queryClient.invalidateQueries({ queryKey: ['repos'] });
    });

    let active = true;
    conn
      .start()
      .then(() => {
        if (active) setConnected(true);
      })
      .catch(() => {
        if (active) setConnected(false);
      });

    conn.onreconnected(() => setConnected(true));
    conn.onclose(() => setConnected(false));

    return () => {
      active = false;
      conn.stop().catch(() => undefined);
    };
  }, [queryClient]);

  return (
    <RealtimeContext.Provider value={{ connected, lastAnnouncement }}>
      <div className="sr-only" role="status" aria-live="polite" aria-atomic="true">
        {lastAnnouncement}
      </div>
      {children}
    </RealtimeContext.Provider>
  );
};
