import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AiSettings, ApiService } from '../../services/api';

@Component({
  selector: 'app-ai-settings',
  imports: [FormsModule],
  templateUrl: './ai-settings.html',
})
export class AiSettingsComponent implements OnInit {
  settings = signal<AiSettings | null>(null);
  enabled = signal(false);
  endpoint = signal('https://api.openai.com/v1/chat/completions');
  model = signal('gpt-4.1-mini');
  apiKey = signal('');
  loading = signal(false);
  testing = signal(false);
  error = signal<string | null>(null);
  message = signal<string | null>(null);

  constructor(private readonly api: ApiService) {}

  ngOnInit(): void {
    this.loadSettings();
  }

  loadSettings(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getAiSettings().subscribe({
      next: (settings) => {
        this.applySettings(settings);
        this.loading.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not load AI settings.');
        this.loading.set(false);
      },
    });
  }

  saveSettings(): void {
    this.loading.set(true);
    this.error.set(null);
    this.message.set(null);
    this.api.saveAiSettings({
      enabled: this.enabled(),
      endpoint: this.endpoint(),
      modelName: this.model(),
      apiKey: this.apiKey() ? this.apiKey() : null,
    }).subscribe({
      next: (settings) => {
        this.applySettings(settings);
        this.apiKey.set('');
        this.message.set('AI settings saved.');
        this.loading.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not save AI settings.');
        this.loading.set(false);
      },
    });
  }

  testProvider(): void {
    this.testing.set(true);
    this.error.set(null);
    this.message.set(null);
    this.api.testAi().subscribe({
      next: (result) => {
        this.message.set(result.message);
        this.testing.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'AI provider test failed.');
        this.testing.set(false);
      },
    });
  }

  private applySettings(settings: AiSettings): void {
    this.settings.set(settings);
    this.enabled.set(settings.enabled);
    this.endpoint.set(settings.endpoint);
    this.model.set(settings.modelName);
  }
}
