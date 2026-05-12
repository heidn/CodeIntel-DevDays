import * as signalR from '@microsoft/signalr';
import type { AnalysisEvent } from '../types';

export class AnalysisHubClient {
  private connection: signalR.HubConnection;
  private subscribers = new Set<(event: AnalysisEvent) => void>();

  constructor() {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/analysis')
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.connection.on('AnalysisEvent', (event: AnalysisEvent) => {
      this.subscribers.forEach((sub) => sub(event));
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
  }

  async leaveAnalysis(analysisId: string): Promise<void> {
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
