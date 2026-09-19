import { Component, inject } from '@angular/core';
import { ApiService } from '../core/api.service';
import { ChatWidget } from '../chat-widget/chat-widget';

@Component({
  selector: 'app-chatbot-page',
  standalone: true,
  imports: [ChatWidget],
  templateUrl: './chatbot-page.html',
  styleUrl: './chatbot-page.scss'
})
export class ChatbotPage {
  readonly api = inject(ApiService);
}
