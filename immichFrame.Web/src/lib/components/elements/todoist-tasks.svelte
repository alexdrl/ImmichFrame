<script lang="ts">
	import * as api from '$lib/index';
	import { onMount } from 'svelte';
	import { configStore } from '$lib/stores/config.store';
	import { clientIdentifierStore } from '$lib/stores/persist.store';

	api.init();

	// The server keeps the Todoist result for TodoistRefreshMinutes, so this poll costs no Todoist call.
	const pollInterval = 2 * 60 * 1000;

	// Todoist's own priority colors, lighter so they read on the dark card. Index = P1..P4.
	const priorityColors = ['#ff7b72', '#fdba74', '#93c5fd', '#d1d5db'];

	let todoist: api.TodoistTasks | undefined = $state();

	let tasks = $derived(todoist?.tasks ?? []);
	let visible = $derived(!!todoist?.enabled && (tasks.length > 0 || !todoist.hideWhenEmpty));
	let total = $derived(tasks.length + (todoist?.hiddenCount ?? 0));

	// The same gray as the appointments cards (bg-gray-600), with the TodoistOpacity alpha. Below
	// about half opacity the photo shows through, so the text gets a shadow to stay readable.
	let cardStyle = $derived.by(() => {
		const opacity = Math.min(Math.max(todoist?.opacity ?? 90, 0), 100);
		const background = `background-color: color-mix(in oklab, var(--color-gray-600, #4b5563) ${opacity}%, transparent);`;
		return opacity < 50 ? `${background} text-shadow: 0 1px 3px rgb(0 0 0 / 0.8);` : background;
	});

	// "yesterday" / "3 days ago" in the frame's language, from the browser, without a translation table.
	let relativeDay = $derived.by(() => {
		try {
			return new Intl.RelativeTimeFormat($configStore.language || undefined, { numeric: 'auto' });
		} catch {
			return new Intl.RelativeTimeFormat('en', { numeric: 'auto' });
		}
	});

	function ringStyle(priority: number | undefined) {
		const color = priorityColors[Math.min(Math.max((priority ?? 4) - 1, 0), 3)];
		return `border-color: ${color}`;
	}

	function details(task: api.TodoistTask) {
		const parts: string[] = [];
		if (task.dueTime) parts.push(task.dueTime);
		for (const label of task.labels ?? []) parts.push(`@${label}`);
		return parts.join(' · ');
	}

	onMount(() => {
		getTasks();
		const interval = setInterval(() => getTasks(), pollInterval);

		return () => {
			clearInterval(interval);
		};
	});

	async function getTasks() {
		const response = await api.getTodoistTasks({ clientIdentifier: $clientIdentifierStore });
		if (response.status == 200) {
			todoist = response.data;
		}
	}
</script>

{#if visible}
	<div
		id="todoist-tasks"
		class="fixed top-0 left-0 w-auto z-10 text-frame-primary m-5 max-w-[25%] hidden lg:block md:min-w-[15%]"
	>
		<div
			class="mb-2 rounded-md px-3 py-2 flex items-center justify-between gap-3"
			style={cardStyle}
		>
			<p class="font-semibold truncate">{todoist?.projectName ?? 'Todoist'}</p>
			<p class="text-xs">{total}</p>
		</div>

		{#each tasks as task (task.id)}
			<div class="mb-2 text-left rounded-md p-3 flex gap-3 items-start" style={cardStyle}>
				<span
					class="mt-1 shrink-0 w-4 h-4 rounded-full border-2"
					style={ringStyle(task.priority)}
					aria-hidden="true"
				></span>
				<div class="min-w-0">
					<p class="leading-snug">{task.content}</p>
					<div class="flex flex-wrap items-center gap-x-2 gap-y-1 mt-1 text-xs empty:hidden">
						{#if task.isOverdue}
							<span class="bg-red-700 text-white font-semibold rounded px-1.5 py-0.5">
								{relativeDay.format(-(task.daysOverdue ?? 0), 'day')}
							</span>
						{/if}
						{#if details(task)}
							<span>{details(task)}</span>
						{/if}
						{#if task.isRecurring}
							<svg
								class="w-3.5 h-3.5"
								viewBox="0 0 24 24"
								fill="none"
								stroke="currentColor"
								stroke-width="2"
								stroke-linecap="round"
								stroke-linejoin="round"
								role="img"
								aria-label="recurring"
							>
								<path d="M17 2l4 4-4 4"></path>
								<path d="M3 11v-1a4 4 0 0 1 4-4h14"></path>
								<path d="M7 22l-4-4 4-4"></path>
								<path d="M21 13v1a4 4 0 0 1-4 4H3"></path>
							</svg>
						{/if}
					</div>
				</div>
			</div>
		{/each}

		{#if todoist?.hiddenCount}
			<p class="text-xs px-1">+{todoist.hiddenCount}</p>
		{/if}
	</div>
{/if}
