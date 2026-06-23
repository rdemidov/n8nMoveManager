import { DatePipe } from '@angular/common';
import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest } from 'rxjs';
import { AiAssistantResponse, AiSettings, ApiService, GitCommit, GitDiffFile, NodeSemanticDiff, WorkflowSemanticDiff, WorkflowSemanticDiffCollection } from '../../services/api';

@Component({
  selector: 'app-commit-diff',
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './commit-diff.html',
})
export class CommitDiffComponent implements OnInit {
  sha = signal<string | null>(null);
  commit = signal<GitCommit | null>(null);
  files = signal<GitDiffFile[]>([]);
  semanticDiff = signal<WorkflowSemanticDiffCollection | null>(null);
  viewMode = signal<'summary' | 'nodes' | 'raw'>('summary');
  error = signal<string | null>(null);
  semanticError = signal<string | null>(null);
  environmentKey = signal('local');
  aiSettings = signal<AiSettings | null>(null);
  aiAnswer = signal<AiAssistantResponse | null>(null);
  aiQuestion = signal('');
  aiLoading = signal(false);
  aiError = signal<string | null>(null);

  constructor(
    readonly api: ApiService,
    private readonly route: ActivatedRoute,
  ) {}

  ngOnInit(): void {
    this.loadAiSettings();
    combineLatest([this.route.paramMap, this.route.queryParamMap]).subscribe(([params, queryParams]) => {
      const sha = params.get('sha');
      const environmentKey = queryParams.get('environment') ?? this.api.selectedEnvironmentKey();
      this.environmentKey.set(environmentKey);
      this.api.selectEnvironment(environmentKey);
      this.sha.set(sha);
      this.loadCommitDetails(sha, environmentKey);
      const request = sha && sha !== 'latest'
        ? this.api.getCommitDiff(sha, environmentKey)
        : this.api.getLatestDiff(environmentKey);

      request.subscribe({
        next: (files) => this.files.set(files),
        error: (response) => {
          this.files.set([]);
          this.error.set(response?.error?.error ?? 'Could not load diff.');
        },
      });

      if (!sha || sha === 'latest') {
        this.semanticDiff.set(null);
        this.semanticError.set('Semantic diff is available for a concrete commit. Open a commit from history to compare it with its parent.');
        return;
      }

      this.api.getCommitSemanticDiff(sha, environmentKey).subscribe({
        next: (semanticDiff) => {
          this.semanticDiff.set(semanticDiff);
          this.semanticError.set(null);
        },
        error: (response) => {
          this.semanticDiff.set(null);
          this.semanticError.set(response?.error?.error ?? 'Could not load semantic diff.');
        },
      });
    });
  }

  workflowPath(workflow: WorkflowSemanticDiff): string {
    return workflow.newFilePath ?? workflow.oldFilePath ?? 'No file path';
  }

  nodeChangeCount(node: NodeSemanticDiff): number {
    return node.parameterChanges.length + node.credentialChanges.length + node.metadataChanges.length;
  }

  totalNodeChanges(): number {
    return (this.semanticDiff()?.workflows ?? []).reduce(
      (total, workflow) => total + workflow.summary.addedNodes + workflow.summary.removedNodes + workflow.summary.modifiedNodes,
      0);
  }

  summarizeChanges(): void {
    const diff = this.semanticDiff();
    if (!diff) {
      this.aiError.set('Open a concrete commit with semantic diff before asking AI.');
      return;
    }

    this.runAi(() => this.api.summarizeWorkflowDiff({
      environmentKey: this.api.selectedEnvironmentKey(),
      workflowFilePath: null,
      workflowId: null,
      diffContext: diff,
    }));
  }

  findRisks(): void {
    const diff = this.semanticDiff();
    if (!diff) {
      this.aiError.set('Open a concrete commit with semantic diff before asking AI.');
      return;
    }

    this.runAi(() => this.api.askAi({
      question: 'Find likely risks in this workflow diff. Focus on endpoints, auth, SQL, webhooks, Execute Workflow references, active state, and credentials.',
      scope: 'current workflow diff',
      environmentKey: this.api.selectedEnvironmentKey(),
      diffContext: diff,
    }));
  }

  explainCredentials(): void {
    const diff = this.semanticDiff();
    if (!diff) {
      this.aiError.set('Open a concrete commit with semantic diff before asking AI.');
      return;
    }

    this.runAi(() => this.api.askAi({
      question: 'Explain credential changes and any mapping or review risks in this diff.',
      scope: 'current workflow diff',
      environmentKey: this.api.selectedEnvironmentKey(),
      diffContext: diff,
    }));
  }

  askAi(): void {
    const question = this.aiQuestion().trim();
    const diff = this.semanticDiff();
    if (!question || !diff) {
      this.aiError.set('Enter a question and open a semantic diff first.');
      return;
    }

    this.runAi(() => this.api.askAi({
      question,
      scope: 'current workflow diff',
      environmentKey: this.api.selectedEnvironmentKey(),
      diffContext: diff,
    }));
  }

  private loadAiSettings(): void {
    this.api.getAiSettings().subscribe({
      next: (settings) => this.aiSettings.set(settings),
      error: () => this.aiSettings.set(null),
    });
  }

  private loadCommitDetails(sha: string | null, environmentKey: string): void {
    this.api.getCommits(100, environmentKey).subscribe({
      next: (commits) => {
        const commit = sha && sha !== 'latest'
          ? commits.find((item) => item.sha === sha || item.shortSha === sha)
          : commits[0];
        this.commit.set(commit ?? null);
      },
      error: () => this.commit.set(null),
    });
  }

  private runAi(request: () => ReturnType<ApiService['askAi']>): void {
    this.aiLoading.set(true);
    this.aiError.set(null);
    request().subscribe({
      next: (answer) => {
        this.aiAnswer.set(answer);
        this.aiLoading.set(false);
      },
      error: (response) => {
        this.aiError.set(response?.error?.error ?? 'AI assistant request failed.');
        this.aiLoading.set(false);
      },
    });
  }
}
