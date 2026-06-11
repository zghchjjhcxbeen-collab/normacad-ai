"""add user org_type

Тип пользователя (individual|bureau|enterprise) — определяет бэкенд ИИ-чата:
облако только для individual, bureau/enterprise — локально/выкл (приватность).

Revision ID: b2c3d4e5f6a7
Revises: 6a191809dd51
Create Date: 2026-06-11 18:00:00.000000
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = 'b2c3d4e5f6a7'
down_revision: Union[str, None] = '6a191809dd51'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    # server_default — чтобы существующие строки получили значение; batch — для SQLite.
    with op.batch_alter_table('users', schema=None) as batch_op:
        batch_op.add_column(
            sa.Column('org_type', sa.String(length=16), nullable=False,
                      server_default='individual')
        )


def downgrade() -> None:
    with op.batch_alter_table('users', schema=None) as batch_op:
        batch_op.drop_column('org_type')
