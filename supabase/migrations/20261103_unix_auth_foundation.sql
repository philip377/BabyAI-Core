-- UNIX central identity foundation. Apply in a dedicated Supabase project.
-- Supabase Auth owns credentials; never store passwords in public tables.
create schema if not exists unix;
create table if not exists unix.profiles (
  user_id uuid primary key references auth.users(id) on delete cascade,
  display_name text not null default '',
  status text not null default 'active' check (status in ('active','suspended')),
  created_at timestamptz not null default now()
);
create table if not exists unix.roles (
  role_key text primary key check (role_key ~ '^[a-z][a-z0-9_]*$'),
  description text not null
);
create table if not exists unix.permissions (
  permission_key text primary key,
  description text not null
);
create table if not exists unix.user_roles (
  user_id uuid not null references unix.profiles(user_id) on delete cascade,
  role_key text not null references unix.roles(role_key),
  primary key(user_id,role_key)
);
create table if not exists unix.role_permissions (
  role_key text not null references unix.roles(role_key),
  permission_key text not null references unix.permissions(permission_key),
  primary key(role_key,permission_key)
);
create table if not exists unix.audit_events (
  id bigint generated always as identity primary key,
  actor_id uuid references auth.users(id) on delete set null,
  action text not null,
  target_id uuid,
  metadata jsonb not null default '{}'::jsonb,
  occurred_at timestamptz not null default now()
);
insert into unix.roles(role_key,description) values
 ('owner','Владелец'),('admin','Администратор'),('operator','Оператор')
on conflict do nothing;
insert into unix.permissions(permission_key,description) values
 ('chat.use','Использовать чат'),('ginger.read','Читать Ginger'),
 ('ginger.match.review','Проверять сопоставления'),
 ('ginger.match.apply','Применять сопоставления'),
 ('computer.use','Управлять компьютером'),
 ('users.manage','Управлять пользователями'),
 ('roles.manage','Назначать права'),
 ('audit.read','Просматривать аудит'),
 ('settings.manage','Управлять настройками')
on conflict do nothing;
insert into unix.role_permissions(role_key,permission_key)
select 'owner',permission_key from unix.permissions on conflict do nothing;
insert into unix.role_permissions(role_key,permission_key) values
 ('admin','chat.use'),('admin','ginger.read'),('admin','ginger.match.review'),
 ('operator','chat.use'),('operator','ginger.read')
on conflict do nothing;
-- No client-side direct access to authorization tables.
revoke all on schema unix from public, anon, authenticated;
revoke all on all tables in schema unix from public, anon, authenticated;
alter default privileges in schema unix revoke all on tables from public, anon, authenticated;
alter table unix.profiles enable row level security;
alter table unix.roles enable row level security;
alter table unix.permissions enable row level security;
alter table unix.user_roles enable row level security;
alter table unix.role_permissions enable row level security;
alter table unix.audit_events enable row level security;
-- No policies intentionally: access only through a trusted server-side API.
-- Provision first owner out-of-band after verifying auth.users ID.
-- Never grant Owner based on client-supplied metadata, email or first signup.
