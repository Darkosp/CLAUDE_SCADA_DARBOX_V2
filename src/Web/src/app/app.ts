import { Component, OnInit, inject } from '@angular/core';
import { TagStream } from './tag-stream';
import { formatValue, TagSnapshot } from './tag';

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly stream = inject(TagStream);

  protected readonly tags = this.stream.tags;
  protected readonly state = this.stream.state;

  ngOnInit(): void {
    void this.stream.start();
  }

  protected value(snapshot: TagSnapshot): string {
    return formatValue(snapshot);
  }

  /** Local rendering of a UTC timestamp — values are UTC everywhere until display. */
  protected observedAt(snapshot: TagSnapshot): string {
    const timestamp = new Date(snapshot.sourceTimestampUtc);
    return Number.isNaN(timestamp.getTime()) || timestamp.getFullYear() < 2000
      ? 'never'
      : timestamp.toLocaleTimeString();
  }
}
