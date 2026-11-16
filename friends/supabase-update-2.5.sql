-- Resurge Whiteout 2.5 friends update. Run once in the Supabase SQL editor (safe to run again).
-- Adds: About Me on profiles, messages to any player (not only friends), fast name search,
-- and live delivery (Realtime) for messages and friend requests.

alter table public.players add column if not exists about text not null default ''
  check (char_length(about) <= 300);
grant update (about) on public.players to authenticated;

create index if not exists players_name_prefix on public.players (name_key text_pattern_ops);
create index if not exists messages_recipient_id on public.messages (recipient, id);

-- Anyone may message any player (no friendship needed), but never themselves.
drop policy if exists messages_send on public.messages;
create policy messages_send on public.messages for insert to authenticated
  with check (sender = auth.uid() and read = false and sender <> recipient);

-- Live updates: the launcher listens for new messages and friend requests addressed to it.
do $$
begin
  if not exists (select 1 from pg_publication_tables where pubname = 'supabase_realtime' and schemaname = 'public' and tablename = 'messages') then
    alter publication supabase_realtime add table public.messages;
  end if;
  if not exists (select 1 from pg_publication_tables where pubname = 'supabase_realtime' and schemaname = 'public' and tablename = 'friendships') then
    alter publication supabase_realtime add table public.friendships;
  end if;
end $$;
