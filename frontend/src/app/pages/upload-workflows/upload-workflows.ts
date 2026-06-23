import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AiAssistantResponse, ApiService, UploadResult, WorkflowSemanticDiffCollection } from '../../services/api';

@Component({
  selector: 'app-upload-workflows',
  imports: [FormsModule, RouterLink],
  templateUrl: './upload-workflows.html',
})
export class UploadWorkflowsComponent {
  selectedFiles = signal<FileList | null>(null);
  uploading = signal(false);
  aiLoading = signal(false);
  savingMessage = signal(false);
  result = signal<UploadResult | null>(null);
  semanticDiff = signal<WorkflowSemanticDiffCollection | null>(null);
  aiMessageSuggestion = signal<AiAssistantResponse | null>(null);
  commitMessage = signal('');
  error = signal<string | null>(null);
  aiError = signal<string | null>(null);
  message = signal<string | null>(null);

  constructor(readonly api: ApiService) {}

  onFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFiles.set(input.files);
    this.result.set(null);
    this.semanticDiff.set(null);
    this.aiMessageSuggestion.set(null);
    this.error.set(null);
    this.aiError.set(null);
    this.message.set(null);
  }

  upload(): void {
    const files = this.selectedFiles();
    if (!files || files.length === 0) {
      this.error.set('Choose one or more .json workflow files.');
      return;
    }

    this.uploading.set(true);
    this.error.set(null);
    this.aiError.set(null);
    this.message.set(null);
    this.result.set(null);
    this.semanticDiff.set(null);
    this.aiMessageSuggestion.set(null);

    this.api.uploadFiles(files, this.api.selectedEnvironmentKey(), this.commitMessage()).subscribe({
      next: (result) => {
        this.result.set(result);
        this.commitMessage.set(result.commitMessage ?? this.commitMessage());
        this.message.set(result.commitSha
          ? `Commit created: ${result.commitSha.slice(0, 10)}.`
          : 'Upload completed. No workflow changes were detected, so no commit was created.');
        this.uploading.set(false);
        if (result.commitSha) {
          this.loadCommitDiff(result.commitSha);
        }
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Upload failed.');
        this.uploading.set(false);
      },
    });
  }

  generateCommitMessage(): void {
    const diff = this.semanticDiff();
    if (!diff) {
      this.aiError.set('Upload changed workflows first so AI can read the diff against the previous commit.');
      return;
    }

    this.aiLoading.set(true);
    this.aiError.set(null);
    this.aiMessageSuggestion.set(null);
    this.api.askAi({
      question: 'Generate one concise Git commit subject for this workflow import diff. Use imperative mood, mention the most important workflow names or change types, and return the subject in the answer field only.',
      scope: 'upload commit message',
      environmentKey: this.api.selectedEnvironmentKey(),
      diffContext: diff,
    }).subscribe({
      next: (answer) => {
        this.aiMessageSuggestion.set(answer);
        this.commitMessage.set(this.cleanCommitMessage(answer.answer));
        this.aiLoading.set(false);
      },
      error: (response) => {
        this.aiError.set(response?.error?.error ?? 'AI could not generate a commit message.');
        this.aiLoading.set(false);
      },
    });
  }

  updateCommitMessage(): void {
    const result = this.result();
    const message = this.commitMessage().trim();
    if (!result?.commitSha || !message) {
      this.error.set('A committed upload and commit message are required.');
      return;
    }

    this.savingMessage.set(true);
    this.error.set(null);
    this.message.set(null);
    this.api.updateCommitMessage(this.api.selectedEnvironmentKey(), result.commitSha, message).subscribe({
      next: (commit) => {
        this.result.set({ ...result, commitSha: commit.sha, commitMessage: commit.message });
        this.commitMessage.set(commit.message);
        this.message.set('Commit message updated.');
        this.savingMessage.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not update commit message.');
        this.savingMessage.set(false);
      },
    });
  }

  private loadCommitDiff(commitSha: string): void {
    this.api.getCommitSemanticDiff(commitSha, this.api.selectedEnvironmentKey()).subscribe({
      next: (diff) => this.semanticDiff.set(diff),
      error: () => this.semanticDiff.set(null),
    });
  }

  private cleanCommitMessage(value: string): string {
    return value
      .split(/\r?\n/)[0]
      .replace(/^["'`]+|["'`]+$/g, '')
      .trim()
      .slice(0, 200);
  }
}
