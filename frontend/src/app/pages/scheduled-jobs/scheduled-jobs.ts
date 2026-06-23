import { DatePipe } from '@angular/common';
import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, EnvironmentItem, ScheduledJob, ScheduledJobRequest, ScheduledJobRun, ScheduledJobRunSummary } from '../../services/api';
import { ConfirmationService } from '../../services/confirmation';
import { ToastService } from '../../services/toast';

type ScheduleMode = 'hourly' | 'daily' | 'weekly' | 'custom';

@Component({
  selector: 'app-scheduled-jobs',
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './scheduled-jobs.html',
})
export class ScheduledJobsComponent implements OnInit {
  jobs = signal<ScheduledJob[]>([]);
  environments = signal<EnvironmentItem[]>([]);
  runs = signal<ScheduledJobRunSummary[]>([]);
  selectedJob = signal<ScheduledJob | null>(null);
  selectedRun = signal<ScheduledJobRun | null>(null);
  editingId = signal<string | null>(null);
  error = signal<string | null>(null);
  message = signal<string | null>(null);
  busyId = signal<string | null>(null);
  loadingRuns = signal(false);

  scheduleMode: ScheduleMode = 'daily';
  scheduleTime = '21:00';
  weeklyDay = '1';
  customCron = '0 21 * * *';

  form = {
    name: 'Daily n8n export',
    jobType: 'DockerN8nWorkflowExport',
    environmentId: '',
    timezone: 'Europe/Kyiv',
    isEnabled: true,
    containerName: 'n8n',
    retentionCount: 10,
    includeGitRepo: true,
    includeDatabase: true,
  };

  readonly jobTypes = [
    { value: 'DockerN8nWorkflowExport', label: 'Docker n8n workflow export' },
    { value: 'N8nApiWorkflowSync', label: 'n8n API workflow sync' },
    { value: 'WorkspaceBackup', label: 'Workspace backup' },
  ];

  readonly timezones = ['Europe/Kyiv', 'UTC', 'Europe/London', 'Europe/Berlin', 'America/New_York'];

  constructor(readonly api: ApiService, private readonly confirmation: ConfirmationService, private readonly toast: ToastService) {}

  ngOnInit(): void {
    this.loadEnvironments();
    this.loadJobs();
  }

  loadEnvironments(): void {
    this.api.getEnvironments().subscribe({
      next: (environments) => {
        this.environments.set(environments);
        this.form.environmentId = environments.find((item) => item.key === this.api.selectedEnvironmentKey())?.id
          ?? environments[0]?.id
          ?? '';
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load environments.'),
    });
  }

  loadJobs(): void {
    this.api.getScheduledJobs().subscribe({
      next: (jobs) => this.jobs.set(jobs),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load scheduled jobs.'),
    });
  }

  save(): void {
    this.error.set(null);
    this.message.set(null);
    const request = this.buildRequest();
    const id = this.editingId();
    const call = id ? this.api.updateScheduledJob(id, request) : this.api.createScheduledJob(request);
    call.subscribe({
      next: (job) => {
        this.message.set(`${job.name} saved.`);
        this.reset();
        this.loadJobs();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Scheduled job could not be saved.'),
    });
  }

  edit(job: ScheduledJob): void {
    this.editingId.set(job.id);
    this.selectedJob.set(job);
    this.form.name = job.name;
    this.form.jobType = job.jobType;
    this.form.environmentId = job.environmentId;
    this.form.timezone = job.timezone;
    this.form.isEnabled = job.isEnabled;
    this.customCron = job.cronExpression;
    this.scheduleMode = 'custom';
    const config = this.parseConfig(job.configJson);
    this.form.containerName = config['containerName'] ?? 'n8n';
    this.form.includeGitRepo = config['includeGitRepo'] ?? true;
    this.form.includeDatabase = config['includeDatabase'] ?? true;
    this.form.retentionCount = config['retentionCount'] ?? 10;
    this.loadRuns(job);
  }

  reset(): void {
    this.editingId.set(null);
    this.selectedJob.set(null);
    this.selectedRun.set(null);
    this.runs.set([]);
    this.scheduleMode = 'daily';
    this.scheduleTime = '21:00';
    this.weeklyDay = '1';
    this.customCron = '0 21 * * *';
    this.form = {
      name: 'Daily n8n export',
      jobType: 'DockerN8nWorkflowExport',
      environmentId: this.environments()[0]?.id ?? '',
      timezone: 'Europe/Kyiv',
      isEnabled: true,
      containerName: 'n8n',
      retentionCount: 10,
      includeGitRepo: true,
      includeDatabase: true,
    };
  }

  async remove(job: ScheduledJob): Promise<void> {
    if (!await this.confirmation.confirm({ title: `Delete ${job.name}?`, message: 'Run history is preserved, but this scheduled job will stop permanently.', confirmLabel: 'Delete job', danger: true })) return;

    this.busyId.set(job.id);
    this.api.deleteScheduledJob(job.id).subscribe({
      next: () => {
        this.message.set(`${job.name} deleted.`);
        this.toast.success(`${job.name} deleted.`);
        this.busyId.set(null);
        this.loadJobs();
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Scheduled job could not be deleted.');
        this.busyId.set(null);
      },
    });
  }

  toggle(job: ScheduledJob): void {
    this.busyId.set(job.id);
    const call = job.isEnabled ? this.api.disableScheduledJob(job.id) : this.api.enableScheduledJob(job.id);
    call.subscribe({
      next: (updated) => {
        this.message.set(`${updated.name} ${updated.isEnabled ? 'enabled' : 'disabled'}.`);
        this.busyId.set(null);
        this.loadJobs();
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Scheduled job state could not be changed.');
        this.busyId.set(null);
      },
    });
  }

  runNow(job: ScheduledJob): void {
    this.busyId.set(job.id);
    this.api.runScheduledJobNow(job.id).subscribe({
      next: (result) => {
        this.message.set(result.message);
        this.busyId.set(null);
        this.loadRuns(job);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Scheduled job could not be queued.');
        this.busyId.set(null);
      },
    });
  }

  loadRuns(job: ScheduledJob): void {
    this.selectedJob.set(job);
    this.loadingRuns.set(true);
    this.api.getScheduledJobRuns(job.id).subscribe({
      next: (runs) => {
        this.runs.set(runs);
        this.loadingRuns.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Run history could not be loaded.');
        this.loadingRuns.set(false);
      },
    });
  }

  openRun(run: ScheduledJobRunSummary): void {
    const job = this.selectedJob();
    if (!job) {
      return;
    }

    this.api.getScheduledJobRun(job.id, run.id).subscribe({
      next: (detail) => this.selectedRun.set(detail),
      error: (response) => this.error.set(response?.error?.error ?? 'Run details could not be loaded.'),
    });
  }

  cronPreview(): string {
    return this.buildCronExpression();
  }

  private buildRequest(): ScheduledJobRequest {
    return {
      name: this.form.name.trim(),
      jobType: this.form.jobType,
      environmentId: this.form.environmentId,
      cronExpression: this.buildCronExpression(),
      timezone: this.form.timezone || 'Europe/Kyiv',
      isEnabled: this.form.isEnabled,
      configJson: this.form.jobType === 'DockerN8nWorkflowExport'
        ? JSON.stringify({
          containerName: this.form.containerName.trim(),
          exportWorkflows: true,
          exportCredentials: false,
          commitChanges: true,
          scanCredentials: true,
          deleteTempFiles: true,
        })
        : this.form.jobType === 'N8nApiWorkflowSync'
          ? JSON.stringify({ commitChanges: true })
          : JSON.stringify({
          includeGitRepo: this.form.includeGitRepo,
          includeDatabase: this.form.includeDatabase,
          retentionCount: Number(this.form.retentionCount) || 10,
        }),
    };
  }

  private buildCronExpression(): string {
    if (this.scheduleMode === 'custom') {
      return this.customCron.trim();
    }

    if (this.scheduleMode === 'hourly') {
      return '0 * * * *';
    }

    const [hour, minute] = this.scheduleTime.split(':').map((part) => Number(part));
    if (this.scheduleMode === 'weekly') {
      return `${minute || 0} ${hour || 0} * * ${this.weeklyDay}`;
    }

    return `${minute || 0} ${hour || 0} * * *`;
  }

  private parseConfig(configJson: string): Record<string, any> {
    try {
      return JSON.parse(configJson || '{}');
    } catch {
      return {};
    }
  }
}
