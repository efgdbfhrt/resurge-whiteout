-- Resurge Whiteout friends backend (Supabase).
-- Run once in the Supabase SQL editor. Also enable: Authentication › Sign In / Providers ›
-- "Allow anonymous sign-ins". The launcher signs each local profile in anonymously.

create table if not exists public.players (
  id uuid primary key references auth.users(id) on delete cascade,
  name text not null check (char_length(name) between 3 and 24 and name ~ '^[A-Za-z0-9 _-]+$'),
  name_key text generated always as (lower(name)) stored unique,
  avatar text check (avatar is null or char_length(avatar) <= 90000),
  play_seconds bigint not null default 0 check (play_seconds >= 0),
  best_round int not null default 0 check (best_round between 0 and 10000),
  sessions int not null default 0 check (sessions >= 0),
  status text not null default 'online' check (status in ('online', 'ingame', 'offline')),
  activity text not null default '' check (char_length(activity) <= 80),
  last_seen timestamptz not null default now(),
  created_at timestamptz not null default now()
);

create table if not exists public.friendships (
  requester uuid not null references public.players(id) on delete cascade,
  addressee uuid not null references public.players(id) on delete cascade,
  accepted boolean not null default false,
  created_at timestamptz not null default now(),
  primary key (requester, addressee),
  check (requester <> addressee)
);

create table if not exists public.messages (
  id bigint generated always as identity primary key,
  sender uuid not null references public.players(id) on delete cascade,
  recipient uuid not null references public.players(id) on delete cascade,
  body text not null check (char_length(body) between 1 and 500),
  sent_at timestamptz not null default now(),
  read boolean not null default false
);
create index if not exists messages_pair on public.messages (sender, recipient, id);
create index if not exists messages_inbox on public.messages (recipient, read);

create or replace function public.are_friends(a uuid, b uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (
    select 1 from public.friendships
    where accepted and ((requester = a and addressee = b) or (requester = b and addressee = a)));
$$;

alter table public.players enable row level security;
alter table public.friendships enable row level security;
alter table public.messages enable row level security;

drop policy if exists players_read on public.players;
create policy players_read on public.players for select to authenticated using (true);
drop policy if exists players_insert on public.players;
create policy players_insert on public.players for insert to authenticated with check (id = auth.uid());
drop policy if exists players_update on public.players;
create policy players_update on public.players for update to authenticated using (id = auth.uid()) with check (id = auth.uid());
drop policy if exists players_delete on public.players;
create policy players_delete on public.players for delete to authenticated using (id = auth.uid());

drop policy if exists friendships_read on public.friendships;
create policy friendships_read on public.friendships for select to authenticated
  using (auth.uid() in (requester, addressee));
drop policy if exists friendships_request on public.friendships;
create policy friendships_request on public.friendships for insert to authenticated
  with check (requester = auth.uid() and accepted = false);
drop policy if exists friendships_accept on public.friendships;
create policy friendships_accept on public.friendships for update to authenticated
  using (addressee = auth.uid()) with check (addressee = auth.uid());
drop policy if exists friendships_remove on public.friendships;
create policy friendships_remove on public.friendships for delete to authenticated
  using (auth.uid() in (requester, addressee));

drop policy if exists messages_read on public.messages;
create policy messages_read on public.messages for select to authenticated
  using (auth.uid() in (sender, recipient));
drop policy if exists messages_send on public.messages;
create policy messages_send on public.messages for insert to authenticated
  with check (sender = auth.uid() and read = false and public.are_friends(sender, recipient));
drop policy if exists messages_mark_read on public.messages;
create policy messages_mark_read on public.messages for update to authenticated
  using (recipient = auth.uid()) with check (recipient = auth.uid());

-- Players may only change their own row's editable columns; ids and names of others are never writable.
revoke update on public.players from authenticated;
grant update (avatar, play_seconds, best_round, sessions, status, activity, last_seen) on public.players to authenticated;
revoke update on public.messages from authenticated;
grant update (read) on public.messages to authenticated;
revoke update on public.friendships from authenticated;
grant update (accepted) on public.friendships to authenticated;

-- 2.5: About Me, messages to anyone, name search, live delivery. Kept in its own file so existing
-- projects can run just that part; it is safe to run on a fresh project right after this one.
-- \i supabase-update-2.5.sql   (paste its contents here when using the web SQL editor)
