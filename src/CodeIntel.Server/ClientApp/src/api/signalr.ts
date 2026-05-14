import * as signalR from '@microsoft/signalr';
import type { AnalysisEvent } from '../types';

export class AnalysisHubClient {
  private connection: signalR.HubConnection;
  private subscribers = new Set<(event: AnalysisEvent) => void>();
  // Groups this client joined. SignalR group membership is per-connection on the
  // server, so on `withAutomaticReconnect` we get a fresh ConnectionId and lose
  // every group. We track the IDs here and re-join on `onreconnected` — without
  // this, a transient drop in the middle of a long run silently strands the UI
  // on the last status it saw before the disconnect.
  private joinedAnalysisIds = new Set<string>();

  constructor() {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/analysis')
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.connection.on('AnalysisEvent', (event: AnalysisEvent) => {
      this.subscribers.forEach((sub) => sub(event));
    });

    this.connection.onreconnected(async () => {
      for (const id of this.joinedAnalysisIds) {
        try {
          await this.connection.invoke('JoinAnalysis', id);
        } catch (e) {
          console.warn('Re-join after reconnect failed for', id, e);
        }
      }
    });
  }

  async start(): Promise<void> {
    if (this.connection.state === signalR.HubConnectionState.Disconnected) {
      await this.connection.start();
    }
  }

  async joinAnalysis(analysisId: string): Promise<void> {
    await this.start();
    await this.connection.invoke('JoinAnalysis', analysisId);
    this.joinedAnalysisIds.add(analysisId);
  }

  async leaveAnalysis(analysisId: string): Promise<void> {
    this.joinedAnalysisIds.delete(analysisId);
    if (this.connection.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('LeaveAnalysis', analysisId);
    }
  }

  subscribe(handler: (event: AnalysisEvent) => void): () => void {
    this.subscribers.add(handler);
    return () => this.subscribers.delete(handler);
  }

  async stop(): Promise<void> {
    await this.connection.stop();
  }
}

// Singleton instance shared across the app
let _instance: AnalysisHubClient | null = null;
export function getAnalysisHub(): AnalysisHubClient {
  if (!_instance) _instance = new AnalysisHubClient();
  return _instance;
}
