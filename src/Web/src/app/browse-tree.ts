import { Component, input, output } from '@angular/core';
import { TreeDevice, TreeFolder, TreeTag } from './models';

/** What the operator currently has selected in the tree. */
export interface Selection {
  device: TreeDevice;
  tag: TreeTag | null;
}

/**
 * The hierarchical browse tree (ADR-0001). Folders are organisational only — nothing
 * about a device changes by moving it between them.
 */
@Component({
  selector: 'app-browse-tree',
  imports: [BrowseTree],
  template: `
    @for (folder of folders(); track folder.id) {
      <div class="folder">
        <span class="label">
          {{ folder.name }}
          @if (editable()) {
            <button
              type="button"
              class="remove"
              [attr.aria-label]="'Delete folder ' + folder.name"
              (click)="deleteFolder.emit({ id: folder.id, name: folder.name })">×</button>
          }
        </span>
        @if (folder.folders.length === 0 && folder.devices.length === 0) {
          <p class="none">Empty folder.</p>
        }
        <app-browse-tree
          [folders]="folder.folders"
          [devices]="folder.devices"
          [selectedTagId]="selectedTagId()"
          [editable]="editable()"
          (selected)="selected.emit($event)"
          (addDeviceHere)="addDeviceHere.emit($event)"
          (deleteFolder)="deleteFolder.emit($event)" />
      </div>
    }

    @for (device of devices(); track device.id) {
      <div class="device">
        <button type="button" class="label device-label" (click)="selected.emit({ device, tag: null })">
          {{ device.name }}
          <span class="driver">{{ device.driverKey }}</span>
        </button>

        @for (tag of device.tags; track tag.id) {
          <button
            type="button"
            class="tag"
            [class.selected]="tag.id === selectedTagId()"
            (click)="selected.emit({ device, tag })">
            {{ tag.name }}
            @if (tag.unit) {
              <span class="unit">{{ tag.unit.symbol }}</span>
            }
          </button>
        } @empty {
          <p class="none">No tags yet.</p>
        }
      </div>
    }
  `,
  styles: `
    :host { display: block; }
    .folder, .device { margin-left: 0.75rem; }
    .folder > .label {
      display: block;
      font-size: 0.8rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: #6b7580;
      margin: 0.6rem 0 0.2rem;
    }
    .device-label {
      display: block;
      width: 100%;
      text-align: left;
      background: none;
      border: 0;
      padding: 0.3rem 0;
      font: inherit;
      font-weight: 600;
      color: #1c2024;
      cursor: pointer;
    }
    .driver {
      font-weight: 400;
      font-size: 0.7rem;
      color: #8a94a0;
      margin-left: 0.4rem;
    }
    .tag {
      display: block;
      width: 100%;
      text-align: left;
      background: none;
      border: 0;
      border-left: 2px solid #e3e7ec;
      padding: 0.25rem 0.6rem;
      margin-left: 0.4rem;
      font: inherit;
      font-size: 0.85rem;
      color: #3c4550;
      cursor: pointer;
    }
    .tag:hover { background: #eef1f4; }
    .tag.selected { border-left-color: #2f6f4f; background: #e8f2ec; font-weight: 600; }
    .unit { color: #8a94a0; margin-left: 0.3rem; font-size: 0.75rem; }
    .remove {
      background: none;
      border: 0;
      color: #a0a8b0;
      font: inherit;
      line-height: 1;
      padding: 0 0.2rem;
      cursor: pointer;
    }
    .remove:hover { color: #96261f; }
    .none { font-size: 0.75rem; color: #a0a8b0; margin: 0.2rem 0 0 1rem; }
  `,
})
export class BrowseTree {
  readonly folders = input.required<TreeFolder[]>();
  readonly devices = input.required<TreeDevice[]>();
  readonly selectedTagId = input<string | null>(null);

  /** Whether configuration controls are offered — only to an Admin (ADR-0011). */
  readonly editable = input(false);

  readonly selected = output<Selection>();
  readonly addDeviceHere = output<string | null>();
  readonly deleteFolder = output<{ id: string; name: string }>();
}
